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
using Trykatch.Modules;
using Trykatch.Modules.AspNetCore;

namespace Trykatch.IntegrationTests;

[TestClass]
public sealed class ModuleAuthenticationBoundaryTests
{
    [TestMethod]
    public async Task BearerAuthenticationRetainsTokenFreeWritesWhileCookieIdentitiesRequireAntiforgery()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("test-bearer")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test-bearer", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddAntiforgery();
        builder.Services.AddModules(builder.Configuration, [new ProbeModule()]);
        await using WebApplication app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.MapOrganizationModuleEndpoints();
        await app.StartAsync();
        using HttpClient client = app.GetTestClient();

        using HttpResponseMessage anonymous = await client.PostAsync("/api/v1/antiforgery-probe", null);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        client.DefaultRequestHeaders.Authorization = new("Bearer", "regression-test");
        using HttpResponseMessage bearer = await client.PostAsync("/api/v1/antiforgery-probe", null);
        bearer.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // A simultaneous cookie identity must not let a bearer identity bypass
        // request verification for ambient browser credentials.
        client.DefaultRequestHeaders.Add("X-Test-Cookie-Identity", "true");
        using HttpResponseMessage mixed = await client.PostAsync("/api/v1/antiforgery-probe", null);
        mixed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using HttpResponseMessage read = await client.GetAsync("/api/v1/antiforgery-probe");
        read.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers.Authorization != "Bearer regression-test")
                return Task.FromResult(AuthenticateResult.NoResult());

            ClaimsPrincipal principal = new(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "service-client")], Scheme.Name));
            if (Request.Headers.ContainsKey("X-Test-Cookie-Identity"))
                principal.AddIdentity(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "browser-user")], IdentityConstants.ApplicationScheme));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    private sealed class ProbeModule : IModule, IOrganizationEndpointContributor
    {
        public ModuleDescriptor Descriptor { get; } = new(
            "antiforgery-probe", "Antiforgery probe", "1.0.0", "Exercises the shared HTTP boundary.",
            [], [], ModuleCapabilities.Api, []);

        public string ModuleId => Descriptor.Id;

        public void Register(IServiceCollection services, IConfiguration configuration) =>
            services.AddSingleton<IOrganizationEndpointContributor>(this);

        public void MapEndpoints(RouteGroupBuilder organizationApi)
        {
            // Modules receive protection even when they omit per-endpoint metadata.
            organizationApi.MapPost("/antiforgery-probe", () => Results.NoContent());
            organizationApi.MapGet("/antiforgery-probe", () => Results.NoContent());
        }
    }
}
