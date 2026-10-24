using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Trykatch.Application.Authorization;
using Trykatch.Application.Common;
using Trykatch.Application.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;
using Trykatch.Modules.AspNetCore;
using Trykatch.Modules.Projects.Application;
using static Trykatch.IntegrationTests.AccountSecurityStepUpTests;

namespace Trykatch.IntegrationTests;

[TestClass, TestCategory("Integration"), DoNotParallelize]
public sealed class ModuleIdempotencyFailureIntegrationTests
{
    private const string FailurePath = "/api/v1/idempotency-failure-probe";
    private const string FailureBody = "{\"name\":\"Synthetic failure project\",\"description\":\"same mutation intent\"}";
    private static SecurityHost host = null!;
    private static HttpClient browser = null!;
    private static FailureModeController failureModes = null!;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext _)
    {
        host = await SecurityHost.StartAsync();
        failureModes = new FailureModeController();
        await host.RestartAsync(new Dictionary<string, string?>
        {
            ["AtomicMutationResponse:MaximumBytes"] = "1024"
        }, "Development", services =>
        {
            services.AddSingleton(failureModes);
            services.AddScoped<FailureModeState>();
            services.AddSingleton<IOrganizationEndpointContributor, FailureProbeEndpoints>();
        });
        browser = await host.SignInAsync();
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        browser?.Dispose();
        if (host is not null) await host.DisposeAsync();
    }

    [TestMethod]
    public async Task NonSuccessAndTransientResponsesRollbackAndAllowTheSameRequestToRetry()
    {
        Guid organizationId = await CreateWorkspaceAsync();
        await AssertFailureThenRetryAsync(organizationId, "failed-400-01", FailureMode.BadRequest, HttpStatusCode.BadRequest);
        await AssertFailureThenRetryAsync(organizationId, "failed-500-01", FailureMode.TransientFailure, HttpStatusCode.InternalServerError);
    }

    [TestMethod]
    public async Task ResponseCaptureOverflowSerializationAndStreamingFailuresRollbackAndAllowRetry()
    {
        Guid organizationId = await CreateWorkspaceAsync();
        await AssertFailureThenRetryAsync(organizationId, "overflow-01", FailureMode.ResponseOverflow, HttpStatusCode.InternalServerError);
        await AssertFailureThenRetryAsync(organizationId, "receipt-overflow-01", FailureMode.ReceiptOverflow, HttpStatusCode.InternalServerError);
        await AssertFailureThenRetryAsync(organizationId, "serialize-01", FailureMode.SerializationFailure, HttpStatusCode.InternalServerError);
        await AssertFailureThenRetryAsync(organizationId, "file-01", FailureMode.FileResult, HttpStatusCode.InternalServerError);
        await AssertFailureThenRetryAsync(organizationId, "stream-01", FailureMode.StreamResult, HttpStatusCode.InternalServerError);
        await AssertFailureThenRetryAsync(organizationId, "sse-01", FailureMode.EventStream, HttpStatusCode.InternalServerError);
        await AssertFailureThenRetryAsync(organizationId, "send-file-01", FailureMode.SendFile, HttpStatusCode.InternalServerError);
        await AssertFailureThenRetryAsync(organizationId, "disable-buffering-01", FailureMode.DisableBuffering, HttpStatusCode.InternalServerError);
    }

    [TestMethod]
    public async Task ClientCancellationAfterMutationRollsBackAndAllowsTheSameRequestToRetry()
    {
        Guid organizationId = await CreateWorkspaceAsync();
        ProjectSnapshot before = await ReadProjectSnapshotAsync(organizationId);
        const string key = "cancel-after-create-01";
        failureModes.SetNext(FailureMode.CancelAfterCreate);
        string csrf = await GetCsrfTokenAsync();
        using HttpRequestMessage request = CreateRequest(key, csrf);
        using CancellationTokenSource cancellation = new();
        Task<HttpResponseMessage> pending = browser.SendAsync(request, cancellation.Token);
        await failureModes.WaitUntilReachedAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => pending);
        await failureModes.WaitUntilCancellationObservedAsync(TimeSpan.FromSeconds(10));

        await WaitForRetrySuccessAsync(organizationId, before, key);
    }

    [TestMethod]
    public async Task DeferredDatabaseCommitRejectionRollsBackTheCapturedReceiptAndBusinessEffects()
    {
        Guid organizationId = await CreateWorkspaceAsync();
        await InstallDeferredCommitFailureAsync();
        try
        {
            ProjectSnapshot before = await ReadProjectSnapshotAsync(organizationId);
            const string key = "deferred-commit-reject-01";
            failureModes.SetNext(FailureMode.DeferredCommitRejection);
            using HttpResponseMessage failed = await SendMutationAsync(key);
            failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
            failed.Headers.Location.ShouldBeNull();
            (await failed.Content.ReadAsStringAsync()).ShouldNotContain("Synthetic failure project");
            (await ReadProjectSnapshotAsync(organizationId)).ShouldBe(before);

            await WaitForRetrySuccessAsync(organizationId, before, key);
        }
        finally
        {
            await RemoveDeferredCommitFailureAsync();
        }
    }

    private static async Task AssertFailureThenRetryAsync(
        Guid organizationId,
        string key,
        FailureMode failureMode,
        HttpStatusCode expectedFailure)
    {
        ProjectSnapshot before = await ReadProjectSnapshotAsync(organizationId);
        failureModes.SetNext(failureMode);

        using HttpResponseMessage failed = await SendMutationAsync(key);

        failed.StatusCode.ShouldBe(expectedFailure);
        failed.Headers.Location.ShouldBeNull();
        (await failed.Content.ReadAsStringAsync()).ShouldNotContain("Synthetic failure project");
        (await ReadProjectSnapshotAsync(organizationId)).ShouldBe(before);

        await WaitForRetrySuccessAsync(organizationId, before, key);
    }

    private static async Task WaitForRetrySuccessAsync(Guid organizationId, ProjectSnapshot before, string key)
    {
        using HttpResponseMessage retried = await SendMutationAsync(key);
        retried.StatusCode.ShouldBe(HttpStatusCode.Created);
        byte[] responseBody = await retried.Content.ReadAsByteArrayAsync();
        string contentType = retried.Content.Headers.ContentType!.ToString();
        string location = retried.Headers.Location!.OriginalString;
        using JsonDocument project = JsonDocument.Parse(responseBody);
        Guid projectId = project.RootElement.GetProperty("id").GetGuid();
        location.ShouldBe($"/api/v1/projects/{projectId:D}");
        ProjectSnapshot after = await ReadProjectSnapshotAsync(organizationId);
        after.ShouldBe(new(before.Projects + 1, before.Audits + 1, before.Outbox + 2, before.Receipts + 1));

        using HttpResponseMessage replay = await SendMutationAsync(key);
        replay.StatusCode.ShouldBe(HttpStatusCode.Created);
        replay.Content.Headers.ContentType!.ToString().ShouldBe(contentType);
        replay.Headers.Location!.OriginalString.ShouldBe(location);
        (await replay.Content.ReadAsByteArrayAsync()).SequenceEqual(responseBody).ShouldBeTrue();
        (await ReadProjectSnapshotAsync(organizationId)).ShouldBe(after);
    }

    private static async Task<Guid> CreateWorkspaceAsync() =>
        await WorkspaceSessionLifecycleTests.CreateOrganizationForCurrentUserAsync(
            browser, "idem-fail-" + Guid.NewGuid().ToString("N")[..18]);

    private static async Task<HttpResponseMessage> SendMutationAsync(string key)
    {
        string csrf = await GetCsrfTokenAsync();
        using HttpRequestMessage request = CreateRequest(key, csrf);
        return await browser.SendAsync(request);
    }

    private static HttpRequestMessage CreateRequest(string key, string csrf)
    {
        HttpRequestMessage request = new(HttpMethod.Post, FailurePath)
        {
            Content = new StringContent(FailureBody, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", key);
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        return request;
    }

    private static async Task<string> GetCsrfTokenAsync()
    {
        using HttpResponseMessage response = await browser.GetAsync("/api/v1/auth/antiforgery");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement csrf = await response.Content.ReadFromJsonAsync<JsonElement>();
        return csrf.GetProperty("token").GetString()!;
    }

    private static async Task<ProjectSnapshot> ReadProjectSnapshotAsync(Guid organizationId)
    {
        await using NpgsqlConnection connection = new(host.OwnerConnection);
        await connection.OpenAsync();
        long projects = await ScalarAsync(connection,
            "SELECT count(*) FROM app.projects WHERE \"OrganizationId\" = @organization", organizationId);
        long audits = await ScalarAsync(connection,
            "SELECT count(*) FROM platform.audit_entries WHERE \"OrganizationId\" = @organization AND \"Action\" LIKE 'project.%'", organizationId);
        long outbox = await ScalarAsync(connection, """
            SELECT count(*) FROM platform.outbox_messages
            WHERE ("Type" = 'TrykatchApp.Application.Projects.ProjectChanged'
                   AND "Payload"->>'organizationId' = @organization_text)
               OR ("Type" = 'projects.created'
                   AND "ContextJson"->>'organizationId' = @organization_text)
            """, organizationId, organizationId.ToString());
        long receipts = await ScalarAsync(connection,
            "SELECT count(*) FROM app.mutation_receipts WHERE \"OrganizationId\" = @organization", organizationId);
        return new(projects, audits, outbox, receipts);
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql, Guid organizationId, string? organizationText = null)
    {
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("organization", organizationId);
        if (organizationText is not null) command.Parameters.AddWithValue("organization_text", organizationText);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task InstallDeferredCommitFailureAsync()
    {
        await using NpgsqlConnection connection = new(host.OwnerConnection);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("""
            CREATE OR REPLACE FUNCTION app.trykatch_test_reject_deferred_project_commit() RETURNS trigger
            LANGUAGE plpgsql AS $failure$
            BEGIN
                IF current_setting('app.test_reject_project_commit', true) = 'on' THEN
                    RAISE EXCEPTION 'synthetic deferred project commit rejection' USING ERRCODE = '23514';
                END IF;
                RETURN NULL;
            END
            $failure$;
            CREATE CONSTRAINT TRIGGER trykatch_test_reject_deferred_project_commit
            AFTER INSERT ON app.projects DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW EXECUTE FUNCTION app.trykatch_test_reject_deferred_project_commit();
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RemoveDeferredCommitFailureAsync()
    {
        await using NpgsqlConnection connection = new(host.OwnerConnection);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("""
            DROP TRIGGER IF EXISTS trykatch_test_reject_deferred_project_commit ON app.projects;
            DROP FUNCTION IF EXISTS app.trykatch_test_reject_deferred_project_commit();
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record ProjectSnapshot(long Projects, long Audits, long Outbox, long Receipts);
}

internal enum FailureMode
{
    Success,
    BadRequest,
    TransientFailure,
    CancelAfterCreate,
    ResponseOverflow,
    ReceiptOverflow,
    SerializationFailure,
    DeferredCommitRejection,
    FileResult,
    StreamResult,
    EventStream,
    SendFile,
    DisableBuffering
}

internal sealed class FailureModeController
{
    private readonly Queue<FailureMode> pending = new();
    private TaskCompletionSource reached = NewSignal();
    private TaskCompletionSource cancellationObserved = NewSignal();

    public void SetNext(FailureMode mode)
    {
        lock (pending)
        {
            pending.Enqueue(mode);
            reached = NewSignal();
            cancellationObserved = NewSignal();
        }
    }

    public FailureMode TakeNext()
    {
        lock (pending)
            return pending.Count == 0 ? FailureMode.Success : pending.Dequeue();
    }

    public void MarkReached() => reached.TrySetResult();
    public void MarkCancellationObserved() => cancellationObserved.TrySetResult();

    public async Task WaitUntilReachedAsync(TimeSpan timeout)
    {
        Task signal;
        lock (pending) signal = reached.Task;
        await signal.WaitAsync(timeout);
    }

    public async Task WaitUntilCancellationObservedAsync(TimeSpan timeout)
    {
        Task signal;
        lock (pending) signal = cancellationObserved.Task;
        await signal.WaitAsync(timeout);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class FailureModeState(FailureModeController controller)
{
    public FailureMode Mode { get; } = controller.TakeNext();
}

internal sealed class FailureProbeEndpoints : IOrganizationEndpointContributor
{
    public string ModuleId => "projects";

    public void MapEndpoints(RouteGroupBuilder organizationApi)
    {
        organizationApi.MapPost("/idempotency-failure-probe", HandleAsync)
            .RequireAuthorization("permission:projects.manage")
            .WithMutationIdempotency(ModuleMutationPolicy.Default with { MaximumResponseBytes = 512 }, AuthorizeRequestAsync, AuthorizeReplayAsync)
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true))
            .WithName("Projects_IdempotencyFailureProbe")
            .Produces<ProjectDto>(StatusCodes.Status201Created);
    }

    private static async Task<IResult> HandleAsync(
        CreateProjectCommand command,
        ProjectUseCases projects,
        FailureModeState state,
        FailureModeController controller,
        HttpContext http,
        ApplicationDbContext application,
        CancellationToken cancellationToken)
    {
        Result<ProjectDto> created = await projects.CreateAsync(command, cancellationToken);
        if (!created.IsSuccess || created.Value is null)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: created.ErrorCode);

        ModuleMutationIdempotency.BindResource(http, new MutationResourceReference("projects", created.Value.Id));
        switch (state.Mode)
        {
            case FailureMode.BadRequest:
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "synthetic_bad_request");
            case FailureMode.TransientFailure:
                return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "synthetic_transient_failure");
            case FailureMode.CancelAfterCreate:
                controller.MarkReached();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, http.RequestAborted);
                    throw new InvalidOperationException("A canceled acceptance request unexpectedly continued.");
                }
                catch (OperationCanceledException)
                {
                    controller.MarkCancellationObserved();
                    throw;
                }
            case FailureMode.ResponseOverflow:
                return Results.Text(new string('x', 4_096));
            case FailureMode.ReceiptOverflow:
                return Results.Text(new string('x', 768));
            case FailureMode.SerializationFailure:
                return Results.Json(new ThrowOnJsonSerialization());
            case FailureMode.DeferredCommitRejection:
                await application.Database.ExecuteSqlRawAsync(
                    "SELECT set_config('app.test_reject_project_commit', 'on', true)", cancellationToken);
                return Results.Created($"/api/v1/projects/{created.Value.Id:D}", created.Value);
            case FailureMode.FileResult:
                return Results.File([1, 2, 3, 4], "application/octet-stream", "synthetic.bin");
            case FailureMode.StreamResult:
                return Results.Stream(new MemoryStream([1, 2, 3, 4]), "application/octet-stream");
            case FailureMode.EventStream:
                return Results.Content("event: synthetic\ndata: not captured\n\n", "text/event-stream");
            case FailureMode.SendFile:
                return new SendFileResponse();
            case FailureMode.DisableBuffering:
                return new UnbufferedResponse();
            default:
                return Results.Created($"/api/v1/projects/{created.Value.Id:D}", created.Value);
        }
    }

    private static Task<bool> AuthorizeRequestAsync(
        EndpointFilterInvocationContext _, MutationAuthority authority, CancellationToken __) =>
        Task.FromResult(authority.Permissions.Contains(Permissions.ProjectsManage));

    private static Task<bool> AuthorizeReplayAsync(
        EndpointFilterInvocationContext _, MutationAuthority authority, MutationResourceReference? resource, CancellationToken __) =>
        Task.FromResult(authority.Permissions.Contains(Permissions.ProjectsManage)
            && resource is { ResourceKey: "projects", ResourceId: var id } && id != Guid.Empty);

    private sealed class ThrowOnJsonSerialization
    {
        private readonly InvalidOperationException failure = new("synthetic response serialization failure");
        public string Value => throw failure;
    }

    private sealed class SendFileResponse : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            string path = Path.GetTempFileName();
            try
            {
                await File.WriteAllBytesAsync(path, [1, 2, 3, 4], context.RequestAborted);
                context.Response.ContentType = "application/octet-stream";
                await context.Response.SendFileAsync(path, context.RequestAborted);
            }
            finally { File.Delete(path); }
        }
    }

    private sealed class UnbufferedResponse : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            context.Features.GetRequiredFeature<IHttpResponseBodyFeature>().DisableBuffering();
            await context.Response.WriteAsync("synthetic streamed bytes", context.RequestAborted);
        }
    }
}
