using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trykatch.Modules;
using Trykatch.Modules.AspNetCore;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class ModuleMutationHttpTests
{
    [TestMethod]
    [DataRow("safe")]
    [DataRow("platform")]
    [DataRow("unnamed")]
    [DataRow("unscoped")]
    [DataRow("unknown-module")]
    [DataRow("missing-permission")]
    [DataRow("stream")]
    [DataRow("duplicate")]
    public async Task EnrollmentRejectsAnUnsecuredOrUnsupportedEndpoint(string change)
    {
        await using WebApplication app = Application();
        RouteHandlerBuilder route = change == "safe"
            ? app.MapGet("/mutation", () => Results.Ok()) : app.MapPost("/mutation", () => Results.Ok());
        if (change != "unscoped") route.WithMetadata(new OrganizationScopedMetadata());
        route.WithMetadata(new ModuleEndpointMetadata(change == "unknown-module" ? "unknown" : "projects"));
        if (change != "unnamed") route.WithName("Projects_TestMutation");
        if (change != "missing-permission") route.RequireAuthorization("permission:projects.manage");
        if (change == "platform") route.RequireAuthorization("platform-permission:platform.users.manage");
        if (change == "stream") route.Produces<Stream>();
        route.WithMutationIdempotency(ModuleMutationPolicy.Default, AllowRequestAsync, AllowReplayAsync);
        if (change == "duplicate") route.WithMutationIdempotency(ModuleMutationPolicy.Default, AllowRequestAsync, AllowReplayAsync);
        Should.Throw<InvalidOperationException>(() => Endpoints(app));
    }

    [TestMethod]
    public async Task EnrollmentRequiresBothExplicitAuthorizationGates()
    {
        await using WebApplication app = Application();
        RouteHandlerBuilder route = Route(app);
        Should.Throw<ArgumentNullException>(() => route.WithMutationIdempotency(ModuleMutationPolicy.Default, null!, AllowReplayAsync));
        Should.Throw<ArgumentNullException>(() => route.WithMutationIdempotency(ModuleMutationPolicy.Default, AllowRequestAsync, null!));
    }

    [TestMethod]
    public async Task ReplayRequiresTheModuleGateAndSkipsTheHandler()
    {
        Store store = new(MutationReservationState.Completed);
        await using WebApplication app = Application(store);
        int replayChecks = 0;
        Route(app).WithMutationIdempotency(ModuleMutationPolicy.Default, AllowRequestAsync, (_, authority, resource, _) =>
        {
            authority.ActorId.ShouldBe(store.Authority.ActorId);
            resource.ShouldBe(store.Receipt.ResourceReference);
            replayChecks++;
            return Task.FromResult(true);
        });
        DefaultHttpContext http = Context(app);
        int handlerCalls = 0;
        object? result = await new ModuleMutationIdempotencyFilter().InvokeAsync(EndpointFilterInvocationContext.Create(http), _ =>
        {
            handlerCalls++;
            return ValueTask.FromResult<object?>(Results.Ok("wrong response"));
        });
        await ((IResult)result!).ExecuteAsync(http);
        replayChecks.ShouldBe(1);
        handlerCalls.ShouldBe(0);
        store.Requests.ShouldBe(1);
        http.Response.StatusCode.ShouldBe(201);
        http.Response.ContentType.ShouldBe("application/json");
        http.Response.Headers.Location.ToString().ShouldBe("/api/v1/projects/original");
        http.Response.Headers.SetCookie.ShouldBeEmpty();
        ((MemoryStream)http.Response.Body).ToArray().ShouldBe(store.Receipt.Body);
    }

    [TestMethod]
    public async Task DeniedReplayDisclosesNeitherOriginalBodyNorLocation()
    {
        Store store = new(MutationReservationState.Completed);
        await using WebApplication app = Application(store);
        Route(app).WithMutationIdempotency(ModuleMutationPolicy.Default, AllowRequestAsync, (_, _, _, _) => Task.FromResult(false));
        DefaultHttpContext http = Context(app);
        object? result = await new ModuleMutationIdempotencyFilter().InvokeAsync(EndpointFilterInvocationContext.Create(http),
            _ => throw new InvalidOperationException("Replay cannot invoke the mutation."));
        await ((IResult)result!).ExecuteAsync(http);
        http.Response.StatusCode.ShouldBe(403);
        http.Response.Headers.Location.ShouldBeEmpty();
        Encoding.UTF8.GetString(((MemoryStream)http.Response.Body).ToArray()).ShouldNotContain("private-original-response");
    }

    [TestMethod]
    [DataRow(MutationReservationState.InProgress, 409, "idempotency_in_progress")]
    [DataRow(MutationReservationState.Conflict, 409, "idempotency_conflict")]
    [DataRow(MutationReservationState.Denied, 403, "forbidden")]
    public async Task ReservationOutcomesDoNotInvokeTheHandler(MutationReservationState state, int status, string code)
    {
        Store store = new(state);
        await using WebApplication app = Application(store);
        Route(app).WithMutationIdempotency(ModuleMutationPolicy.Default, AllowRequestAsync, AllowReplayAsync);
        DefaultHttpContext http = Context(app);
        object? result = await new ModuleMutationIdempotencyFilter().InvokeAsync(EndpointFilterInvocationContext.Create(http),
            _ => throw new InvalidOperationException("This outcome cannot invoke the mutation."));
        await ((IResult)result!).ExecuteAsync(http);
        http.Response.StatusCode.ShouldBe(status);
        Encoding.UTF8.GetString(((MemoryStream)http.Response.Body).ToArray()).ShouldContain(code);
        http.Response.Headers.RetryAfter.ToString().ShouldBe(state == MutationReservationState.InProgress ? "1" : "");
    }

    [TestMethod]
    public async Task ReservedResponseCapturesExecutedBytesAndServerResourceBeforeFinalization()
    {
        Store store = new(MutationReservationState.Reserved);
        await using WebApplication app = Application(store);
        Route(app).WithMutationIdempotency(ModuleMutationPolicy.Default, AllowRequestAsync, AllowReplayAsync);
        DefaultHttpContext http = Context(app);
        MutationResourceReference resource = new("projects", Guid.NewGuid());
        await AtomicMutationResponse.ExecuteAsync(http, 1_048_576, async context =>
        {
            object? result = await new ModuleMutationIdempotencyFilter().InvokeAsync(EndpointFilterInvocationContext.Create(context), _ =>
            {
                ModuleMutationIdempotency.BindResource(context, resource);
                return ValueTask.FromResult<object?>(Results.Created("/api/v1/projects/new", new { id = resource.ResourceId }));
            });
            await ((IResult)result!).ExecuteAsync(context);
        }, _ =>
        {
            store.Completed.ShouldNotBeNull();
            store.Completed.ResourceReference.ShouldBe(resource);
            ((MemoryStream)http.Response.Body).Length.ShouldBeGreaterThan(0);
            return Task.CompletedTask;
        });
        store.Completed!.Body.ShouldBe(((MemoryStream)http.Response.Body).ToArray());
        store.Completed.Location.ShouldBe("/api/v1/projects/new");
    }

    [TestMethod]
    public async Task NonSuccessfulEnrolledResponseRequiresWholeTransactionRollback()
    {
        Store store = new(MutationReservationState.Reserved);
        await using WebApplication app = Application(store);
        Route(app).WithMutationIdempotency(ModuleMutationPolicy.Default, AllowRequestAsync, AllowReplayAsync);
        DefaultHttpContext http = Context(app);
        await AtomicMutationResponse.ExecuteAsync(http, 1_048_576, async context =>
        {
            object? result = await new ModuleMutationIdempotencyFilter().InvokeAsync(EndpointFilterInvocationContext.Create(context),
                _ => ValueTask.FromResult<object?>(Results.BadRequest("validation")));
            await ((IResult)result!).ExecuteAsync(context);
        }, _ =>
        {
            (http.Features.Get<IModuleMutationResponseCaptureFeature>()?.RequiresRollback).ShouldBe(true);
            store.Completed.ShouldBeNull();
            return Task.CompletedTask;
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReturnedFileOrEventStreamCannotBeCapturedAsAnOrdinaryReceipt(bool eventStream)
    {
        Store store = new(MutationReservationState.Reserved);
        await using WebApplication app = Application(store);
        Route(app).WithMutationIdempotency(ModuleMutationPolicy.Default, AllowRequestAsync, AllowReplayAsync);
        DefaultHttpContext http = Context(app);
        bool finalized = false;
        await Should.ThrowAsync<NotSupportedException>(() => AtomicMutationResponse.ExecuteAsync(http, 1024, async context =>
        {
            object? result = await new ModuleMutationIdempotencyFilter().InvokeAsync(EndpointFilterInvocationContext.Create(context),
                _ => ValueTask.FromResult<object?>(eventStream
                    ? Results.Text("data: private-event\n\n", "text/event-stream")
                    : Results.File("private-file"u8.ToArray(), "application/octet-stream")));
            await ((IResult)result!).ExecuteAsync(context);
        }, _ => { finalized = true; return Task.CompletedTask; }));
        finalized.ShouldBeFalse();
        store.Completed.ShouldBeNull();
    }

    [TestMethod]
    public async Task RequiredKeyIsRejectedBeforeBindingOrReservation()
    {
        Store store = new(MutationReservationState.Reserved);
        await using WebApplication app = Application(store);
        Route(app).WithMutationIdempotency(ModuleMutationPolicy.Default with { KeyRequired = true }, AllowRequestAsync, AllowReplayAsync);
        DefaultHttpContext http = Context(app);
        http.Request.Headers.Remove("Idempotency-Key");
        http.Features.Set<ModulePreparedMutationRequest>(null);
        ApplicationBuilder pipeline = new(app.Services);
        pipeline.UseModuleMutationIdempotency();
        pipeline.Run(_ => throw new InvalidOperationException("Missing required keys must fail before binding."));
        await pipeline.Build()(http);
        http.Response.StatusCode.ShouldBe(400);
        Encoding.UTF8.GetString(((MemoryStream)http.Response.Body).ToArray()).ShouldContain("idempotency_key_required");
        store.Requests.ShouldBe(0);
    }

    [TestMethod]
    public async Task DuplicateJsonIsRejectedByPreparationBeforeBinding()
    {
        Store store = new(MutationReservationState.Reserved);
        await using WebApplication app = Application(store);
        Route(app).WithMutationIdempotency(ModuleMutationPolicy.Default, AllowRequestAsync, AllowReplayAsync);
        DefaultHttpContext http = Context(app);
        http.Features.Set<ModulePreparedMutationRequest>(null);
        byte[] body = Encoding.UTF8.GetBytes("{\"name\":\"one\",\"name\":\"two\"}");
        http.Request.Body = new MemoryStream(body);
        http.Request.ContentType = "application/json";
        http.Request.ContentLength = body.Length;
        ApplicationBuilder pipeline = new(app.Services);
        pipeline.UseModuleMutationIdempotency();
        pipeline.Run(_ => throw new InvalidOperationException("Malformed intent must fail before binding."));
        await pipeline.Build()(http);
        http.Response.StatusCode.ShouldBe(400);
        store.Requests.ShouldBe(0);
    }

    [TestMethod]
    [DataRow(204)]
    [DataRow(205)]
    public async Task BodylessStatusesCannotCommitNonemptyReceiptBytes(int status)
    {
        Store store = new(MutationReservationState.Reserved);
        await using WebApplication app = Application(store);
        Route(app).WithMutationIdempotency(ModuleMutationPolicy.Default, AllowRequestAsync, AllowReplayAsync);
        DefaultHttpContext http = Context(app);
        MemoryStream client = (MemoryStream)http.Response.Body;
        bool finalized = false;
        await Should.ThrowAsync<InvalidOperationException>(() => AtomicMutationResponse.ExecuteAsync(http, 1024, async context =>
        {
            await new ModuleMutationIdempotencyFilter().InvokeAsync(EndpointFilterInvocationContext.Create(context), async _ =>
            {
                context.Response.StatusCode = status;
                await context.Response.WriteAsync("invalid body");
                return null;
            });
        }, _ => { finalized = true; return Task.CompletedTask; }));
        finalized.ShouldBeFalse();
        store.Completed.ShouldBeNull();
        client.Length.ShouldBe(0);
    }

    [TestMethod]
    public async Task CaptureUsesTheSmallerOuterStagingLimit()
    {
        Store store = new(MutationReservationState.Reserved);
        await using WebApplication app = Application(store);
        Route(app).WithMutationIdempotency(ModuleMutationPolicy.Default, AllowRequestAsync, AllowReplayAsync);
        DefaultHttpContext http = Context(app);
        await AtomicMutationResponse.ExecuteAsync(http, 8, async context =>
        {
            await new ModuleMutationIdempotencyFilter().InvokeAsync(EndpointFilterInvocationContext.Create(context), async _ =>
            {
                await context.Response.WriteAsync("{}");
                return null;
            });
        }, _ => Task.CompletedTask);
        store.Completed.ShouldNotBeNull();
        store.Completed.Body.ShouldBe("{}"u8.ToArray());
    }

    private static WebApplication Application(Store? store = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(new ModuleCatalog([new ProjectsModule()]));
        builder.Services.AddSingleton<IModuleMutationIdempotencyStore>(store ?? new(MutationReservationState.Reserved));
        return builder.Build();
    }

    private static RouteHandlerBuilder Route(WebApplication app) => app.MapPost("/mutation", () => Results.Ok())
        .WithMetadata(new OrganizationScopedMetadata(), new ModuleEndpointMetadata("projects"))
        .RequireAuthorization("permission:projects.manage").WithName("Projects_TestMutation");

    private static Endpoint[] Endpoints(WebApplication app) => ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).ToArray();

    private static DefaultHttpContext Context(WebApplication app)
    {
        DefaultHttpContext http = new() { RequestServices = app.Services };
        http.SetEndpoint(Endpoints(app).Single());
        http.Request.Method = "POST";
        http.Request.Headers["Idempotency-Key"] = "unit-key";
        http.Features.Set(new ModulePreparedMutationRequest(new string('A', 64), new string('B', 64)));
        http.Response.Body = new MemoryStream();
        return http;
    }

    private static Task<bool> AllowRequestAsync(EndpointFilterInvocationContext _, MutationAuthority __, CancellationToken ___) => Task.FromResult(true);
    private static Task<bool> AllowReplayAsync(EndpointFilterInvocationContext _, MutationAuthority __, MutationResourceReference? ___, CancellationToken ____) => Task.FromResult(true);

    private sealed class Store(MutationReservationState state) : IModuleMutationIdempotencyStore
    {
        public MutationAuthority Authority { get; } = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new HashSet<string> { "projects.manage" });
        public MutationReceipt Receipt { get; } = new(201, "application/json", "/api/v1/projects/original",
            Encoding.UTF8.GetBytes("private-original-response"), new("projects", Guid.NewGuid()));
        public MutationReceipt? Completed { get; private set; }
        public int Requests { get; private set; }

        public async Task<MutationReservationResult> ReserveAsync(MutationRequest request, Func<MutationAuthority, CancellationToken, Task<bool>> gate, CancellationToken cancellationToken = default)
        {
            Requests++;
            if (!await gate(Authority, cancellationToken)) return new(MutationReservationState.Denied);
            return new(state, Authority, state == MutationReservationState.Reserved ? Guid.NewGuid() : null,
                state == MutationReservationState.Completed ? Receipt : null);
        }

        public Task CompleteAsync(Guid reservationId, MutationReceipt receipt, CancellationToken cancellationToken = default)
        {
            Completed = receipt;
            return Task.CompletedTask;
        }
    }
}
