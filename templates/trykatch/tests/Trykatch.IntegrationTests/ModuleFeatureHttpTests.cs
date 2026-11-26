using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Trykatch.Api.Security;
using Trykatch.Modules;
using Trykatch.Modules.AspNetCore;

namespace Trykatch.IntegrationTests;

[TestClass]
public sealed class ModuleFeatureHttpTests
{
    [TestMethod]
    [DataRow("unknown")]
    [DataRow("foreign")]
    [DataRow("raw-foreign")]
    [DataRow("malformed")]
    [DataRow("duplicate")]
    [DataRow("anonymous")]
    [DataRow("unscoped")]
    [DataRow("fake-scope")]
    [DataRow("unknown-owner")]
    [DataRow("platform")]
    public async Task CompositionRejectsFeatureMetadataOutsideItsInstalledSecuredOwner(string change)
    {
        await using WebApplication app = Application(new Probe(change));
        if (change is "unscoped" or "fake-scope")
        {
            RouteHandlerBuilder route = app.MapGet("/unscoped", () => Results.Ok())
                .WithMetadata(new ModuleEndpointMetadata("feature-probe")).RequireAuthorization();
            if (change == "fake-scope") route.WithMetadata(new OrganizationScopedMetadata());
            route.RequireModuleFeature("feature-probe.reads");
        }
        Should.Throw<InvalidOperationException>(() => ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FeatureFilterRunsAfterCsrfAndBeforeReplayRegardlessOfEnrollmentOrder(bool featureFirst)
    {
        Probe probe = new(featureFirst ? "feature-first" : "replay-first") { Enabled = false };
        await using WebApplication app = Application(probe);
        await app.StartAsync();
        using HttpClient client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "feature-test");
        client.DefaultRequestHeaders.Add("X-Test-Cookie-Identity", "true");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "feature-order-key");
        using (HttpResponseMessage invalidCsrf = await client.PostAsync("/api/v1/feature-probe/mutation", null))
            invalidCsrf.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        probe.Evaluations.ShouldBe(0);
        probe.Reservations.ShouldBe(0);

        client.DefaultRequestHeaders.Remove("X-Test-Cookie-Identity");
        using (HttpResponseMessage disabled = await client.PostAsync("/api/v1/feature-probe/mutation", null))
        {
            disabled.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await disabled.Content.ReadAsStringAsync()).ShouldContain("feature_unavailable");
        }
        probe.Evaluations.ShouldBe(1);
        probe.Reservations.ShouldBe(0);
        probe.Handlers.ShouldBe(0);

        probe.Enabled = true;
        using HttpResponseMessage reachesReplay = await client.PostAsync("/api/v1/feature-probe/mutation", null);
        reachesReplay.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        probe.Reservations.ShouldBe(1);
    }

    [TestMethod]
    public async Task ReadAvailabilityIsFreshDoesNotGrantPermissionAndOutagesStay503()
    {
        Probe probe = new("valid");
        await using WebApplication app = Application(probe);
        await app.StartAsync();
        using HttpClient client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "feature-test");
        (await client.GetAsync("/api/v1/feature-probe")).StatusCode.ShouldBe(HttpStatusCode.OK);
        probe.Handlers.ShouldBe(1);
        probe.Enabled = false;
        (await client.GetAsync("/api/v1/feature-probe")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        probe.Handlers.ShouldBe(1);
        probe.Enabled = true;
        client.DefaultRequestHeaders.Add("X-Test-No-Permission", "true");
        int before = probe.Evaluations;
        (await client.GetAsync("/api/v1/feature-probe")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        probe.Evaluations.ShouldBe(before);
        probe.Handlers.ShouldBe(1);
        client.DefaultRequestHeaders.Remove("X-Test-No-Permission");
        probe.Fail = true;
        using HttpResponseMessage unavailable = await client.GetAsync("/api/v1/feature-probe");
        unavailable.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await unavailable.Content.ReadAsStringAsync()).ShouldContain("feature_policy_unavailable");
        probe.Handlers.ShouldBe(1);
    }

    private static WebApplication Application(Probe probe)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("feature-test")
            .AddScheme<AuthenticationSchemeOptions, Authentication>("feature-test", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("permission:feature-probe.read", policy =>
            policy.RequireAuthenticatedUser().RequireAssertion(context => !context.User.HasClaim("no-permission", "true"))));
        builder.Services.AddAntiforgery();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<OrganizationFeatureExceptionHandler>();
        builder.Services.AddSingleton<IOrganizationFeatureEvaluator>(probe);
        builder.Services.AddSingleton<IModuleMutationIdempotencyStore>(probe);
        builder.Services.AddModules(builder.Configuration, [probe, new OtherModule()]);
        WebApplication app = builder.Build();
        app.UseExceptionHandler();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.UseModuleMutationIdempotency();
        app.MapOrganizationModuleEndpoints();
        return app;
    }

    private sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers.Authorization != "Bearer feature-test") return Task.FromResult(AuthenticateResult.NoResult());
            List<Claim> claims = [new(ClaimTypes.NameIdentifier, "feature-probe")];
            if (Request.Headers.ContainsKey("X-Test-No-Permission")) claims.Add(new("no-permission", "true"));
            ClaimsPrincipal principal = new(new ClaimsIdentity(claims, Scheme.Name));
            if (Request.Headers.ContainsKey("X-Test-Cookie-Identity"))
                principal.AddIdentity(new ClaimsIdentity(claims, IdentityConstants.ApplicationScheme));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    private sealed class OtherModule : IModule
    {
        public ModuleDescriptor Descriptor { get; } = new("other", "Other", "1.0.0", "Other owner.", [], [], ModuleCapabilities.None, [])
        { Features = [new("other.reads", "Reads", "Read other records.", true)] };
        public void Register(IServiceCollection services, IConfiguration configuration) { }
    }

    private sealed class Probe(string change) : IModule, IOrganizationEndpointContributor, IOrganizationFeatureEvaluator,
        IModuleMutationIdempotencyStore
    {
        public bool Enabled { get; set; } = true;
        public bool Fail { get; set; }
        public int Evaluations { get; private set; }
        public int Handlers { get; private set; }
        public int Reservations { get; private set; }
        public string ModuleId => "feature-probe";
        public ModuleDescriptor Descriptor { get; } = new("feature-probe", "Feature probe", "1.0.0", "Feature boundary.", [], [], ModuleCapabilities.Api, [])
        { Features = [new("feature-probe.reads", "Reads", "Read probe records.", true)] };

        public void Register(IServiceCollection services, IConfiguration configuration) =>
            services.AddSingleton<IOrganizationEndpointContributor>(this);

        public void MapEndpoints(RouteGroupBuilder organizationApi)
        {
            RouteHandlerBuilder read = organizationApi.MapGet("/feature-probe", () => { Handlers++; return Results.Ok(); })
                .WithName("FeatureProbe_Read").RequireAuthorization("permission:feature-probe.read");
            string key = change switch
            {
                "unknown" => "feature-probe.unknown", "foreign" or "raw-foreign" => "other.reads",
                "malformed" => "Malformed Key", _ => "feature-probe.reads"
            };
            if (change == "raw-foreign") read.WithMetadata(new ModuleFeatureRequirement(key));
            else read.RequireModuleFeature(key);
            if (change == "duplicate") read.RequireModuleFeature(key);
            if (change == "anonymous") read.AllowAnonymous();
            if (change == "unknown-owner") read.WithMetadata(new ModuleEndpointMetadata("absent"));
            if (change == "platform") read.RequireAuthorization("platform-permission:platform.features.read");

            RouteHandlerBuilder write = organizationApi.MapPost("/feature-probe/mutation", () => { Handlers++; return Results.NoContent(); })
                .WithName("FeatureProbe_Write").RequireAuthorization("permission:feature-probe.read");
            if (change == "feature-first") write.RequireModuleFeature("feature-probe.reads");
            write.WithMutationIdempotency(ModuleMutationPolicy.Default, (_, _, _) => Task.FromResult(true), (_, _, _, _) => Task.FromResult(true));
            if (change != "feature-first") write.RequireModuleFeature("feature-probe.reads");
        }

        public Task<bool> IsEnabledAsync(string featureKey, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            featureKey.ShouldBe("feature-probe.reads");
            Evaluations++;
            if (Fail) throw new OrganizationFeaturePolicyUnavailableException(new IOException("Probe unavailable."));
            return Task.FromResult(Enabled);
        }

        public Task<MutationReservationResult> ReserveAsync(MutationRequest request,
            Func<MutationAuthority, CancellationToken, Task<bool>> authorizeRequest, CancellationToken cancellationToken = default)
        {
            Reservations++;
            return Task.FromResult(new MutationReservationResult(MutationReservationState.Denied));
        }
        public Task CompleteAsync(Guid reservationId, MutationReceipt receipt, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A denied reservation cannot complete.");
    }
}
