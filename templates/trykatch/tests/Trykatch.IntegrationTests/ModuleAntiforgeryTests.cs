using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Identity;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;
using Trykatch.Modules.Documents.Infrastructure;
using Trykatch.Modules.Federation.Infrastructure;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class ModuleAntiforgeryTests
{
    private const string PlatformEmail = "platform-csrf@trykatch.test";
    private const string TenantEmail = "tenant-csrf@trykatch.test";
    private const string Password = "Local-only!Antiforgery-Password-42";
    private static readonly Guid MissingRecordId = Guid.Parse("00000000-0000-0000-0000-000000000042");

    [TestMethod]
    public async Task EveryModuleMutationRejectsMissingAndInvalidTokensBeforeExecuting()
    {
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f").Build();
        await postgres.StartAsync();
        string connectionString = postgres.GetConnectionString();
        (string organizationConnection, string platformConnection, string identityConnection, string outboxConnection) =
            await PostgresRuntimeRoleFixture.CreateConnectionStringsAsync(connectionString);
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:trykatchdb"] = identityConnection,
            ["ConnectionStrings:trykatch-identity"] = identityConnection,
            ["Federation:AllowInsecureLoopbackIssuer"] = "true"
        }).Build();
        IModule[] modules = [new ProjectsModule(), new DocumentsModule(), new FederationModule()];
        await MigrateAsync(connectionString, configuration, modules);
        await PostgresRuntimeRoleFixture.GrantApplicationPrivilegesAsync(connectionString);

        await using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(webHost =>
            {
                webHost.UseEnvironment("Development");
                webHost.UseSetting("ConnectionStrings:trykatchdb", organizationConnection);
                webHost.UseSetting("ConnectionStrings:trykatch-organization", organizationConnection);
                webHost.UseSetting("ConnectionStrings:trykatch-platform", platformConnection);
                webHost.UseSetting("ConnectionStrings:trykatch-identity", identityConnection);
                webHost.UseSetting("ConnectionStrings:trykatch-outbox", outboxConnection);
                webHost.ConfigureAppConfiguration((_, settings) => settings.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:trykatchdb"] = organizationConnection,
                    ["ConnectionStrings:trykatch-organization"] = organizationConnection,
                    ["ConnectionStrings:trykatch-platform"] = platformConnection,
                    ["ConnectionStrings:trykatch-identity"] = identityConnection,
                    ["ConnectionStrings:trykatch-outbox"] = outboxConnection,
                    ["Bootstrap:PlatformAdminEmail"] = PlatformEmail,
                    ["Bootstrap:PlatformAdminPassword"] = Password,
                    ["DevelopmentDemo:Enabled"] = "true",
                    ["DevelopmentDemo:PlatformAdminEmail"] = PlatformEmail,
                    ["DevelopmentDemo:TenantAdminEmail"] = TenantEmail,
                    ["DevelopmentDemo:Password"] = Password,
                    ["DevelopmentDemo:OrganizationName"] = "Antiforgery workspace",
                    ["DevelopmentDemo:OrganizationSlug"] = "antiforgery-workspace",
                    ["Federation:AllowInsecureLoopbackIssuer"] = "true"
                }));
                webHost.ConfigureTestServices(services =>
                {
                    services.Configure<ProblemDetailsOptions>(options => options.CustomizeProblemDetails = context =>
                        context.ProblemDetails.Extensions["testException"] = context.HttpContext.Features.Get<IExceptionHandlerFeature>()?.Error.ToString());
                    // Federation is enabled only in this test's explicit composition.
                    // Reuse the production module registration and endpoint mapper.
                    services.RemoveAll<ModuleCatalog>();
                    services.AddSingleton(new ModuleCatalog(modules));
                    new FederationModule().Register(services, configuration);
                });
            });

        using HttpClient tenant = CreateClient(factory);
        using HttpClient platform = CreateClient(factory);
        string tenantToken = await LoginAsync(tenant, TenantEmail);
        string platformToken = await LoginAsync(platform, PlatformEmail);
        Guid organizationId;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            organizationId = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>()
                .Organizations.Where(organization => organization.Slug == "antiforgery-workspace")
                .Select(organization => organization.Id).SingleAsync();
        }
        using (HttpResponseMessage selected = await SendAsync(tenant, HttpMethod.Post, "/api/v1/workspace/select",
                   new { organizationId, remember = false }, tenantToken))
            selected.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        Mutation[] mutations = CreateMutations();
        string[] mappedMutations = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                .Any(method => method is "POST" or "PUT" or "DELETE" or "PATCH") == true)
            .Select(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName)
            .OfType<string>()
            .Where(name => name.StartsWith("Projects_", StringComparison.Ordinal)
                || name.StartsWith("Documents_", StringComparison.Ordinal)
                || name.StartsWith("Federation_", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToArray();
        mappedMutations.ShouldBe(mutations.Select(mutation => mutation.OperationId).Order(StringComparer.Ordinal).ToArray());

        foreach (Mutation mutation in mutations)
        {
            HttpClient client = mutation.IsPlatform ? platform : tenant;
            using HttpResponseMessage missing = await SendAsync(client, mutation.Method, mutation.Path, mutation.Body);
            missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"{mutation.OperationId}: {await missing.Content.ReadAsStringAsync()}");
            using JsonDocument missingProblem = JsonDocument.Parse(await missing.Content.ReadAsStringAsync());
            missingProblem.RootElement.GetProperty("title").GetString().ShouldBe("Request verification failed");

            using HttpResponseMessage invalid = await SendAsync(client, mutation.Method, mutation.Path, mutation.Body, "invalid-token");
            invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest, mutation.OperationId);
        }

        // Neither rejected JSON creates nor bodyless mutations may touch data.
        await AssertRecordCountsAsync(connectionString, 0);

        foreach (Mutation mutation in mutations)
        {
            HttpClient client = mutation.IsPlatform ? platform : tenant;
            string token = mutation.IsPlatform ? platformToken : tenantToken;
            using HttpResponseMessage valid = await SendAsync(client, mutation.Method, mutation.Path, mutation.Body, token);
            if (mutation.IsCreate)
                valid.IsSuccessStatusCode.ShouldBeTrue($"{mutation.OperationId}: {await valid.Content.ReadAsStringAsync()}");
            else
                valid.StatusCode.ShouldBe(HttpStatusCode.NotFound, mutation.OperationId);
        }
        await AssertRecordCountsAsync(connectionString, 1);

        // Reads retain their existing token-free behavior.
        (await tenant.GetAsync("/api/v1/projects")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await tenant.GetAsync("/api/v1/documents")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await platform.GetAsync("/api/v1/platform/federation/connections")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static Mutation[] CreateMutations() =>
    [
        new("Projects_Create", HttpMethod.Post, "/api/v1/projects", new { name = "CSRF project", description = "Protected" }, IsCreate: true),
        new("Projects_Update", HttpMethod.Put, $"/api/v1/projects/{MissingRecordId}", new { name = "Changed", description = "Protected" }),
        new("Projects_Archive", HttpMethod.Post, $"/api/v1/projects/{MissingRecordId}/archive"),
        new("Projects_Restore", HttpMethod.Post, $"/api/v1/projects/{MissingRecordId}/restore"),
        new("Projects_Delete", HttpMethod.Delete, $"/api/v1/projects/{MissingRecordId}", new { reason = "Antiforgery regression coverage" }),
        new("Documents_Create", HttpMethod.Post, "/api/v1/documents", new { title = "CSRF document", content = "Protected" }, IsCreate: true),
        new("Documents_Update", HttpMethod.Put, $"/api/v1/documents/{MissingRecordId}", new { title = "Changed", content = "Protected" }),
        new("Documents_Archive", HttpMethod.Post, $"/api/v1/documents/{MissingRecordId}/archive"),
        new("Documents_Restore", HttpMethod.Post, $"/api/v1/documents/{MissingRecordId}/restore"),
        new("Documents_Delete", HttpMethod.Delete, $"/api/v1/documents/{MissingRecordId}", new { reason = "Antiforgery regression coverage" }),
        new("Federation_CreateConnection", HttpMethod.Post, "/api/v1/platform/federation/connections", new
            { name = "CSRF provider", issuer = "http://127.0.0.1:1", clientId = "csrf-test", clientSecret = "local-test-secret" }, true, true),
        new("Federation_UpdateConnection", HttpMethod.Put, $"/api/v1/platform/federation/connections/{MissingRecordId}", new
            { name = "Changed", issuer = "http://127.0.0.1:1", clientId = "csrf-test", clientSecret = "local-test-secret" }, true),
        new("Federation_TestConnection", HttpMethod.Post, $"/api/v1/platform/federation/connections/{MissingRecordId}/test", IsPlatform: true),
        new("Federation_EnableConnection", HttpMethod.Post, $"/api/v1/platform/federation/connections/{MissingRecordId}/enable", IsPlatform: true),
        new("Federation_DisableConnection", HttpMethod.Post, $"/api/v1/platform/federation/connections/{MissingRecordId}/disable", IsPlatform: true),
        new("Federation_DeleteConnection", HttpMethod.Delete, $"/api/v1/platform/federation/connections/{MissingRecordId}", IsPlatform: true)
    ];

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory) => factory.CreateClient(new()
    {
        BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true
    });

    private static async Task<string> LoginAsync(HttpClient client, string email)
    {
        string token = await GetTokenAsync(client);
        using HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, "/api/v1/auth/login",
            new { email, password = Password, rememberMe = false }, token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await GetTokenAsync(client);
    }

    private static async Task<string> GetTokenAsync(HttpClient client)
    {
        using JsonDocument payload = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/antiforgery"));
        return payload.RootElement.GetProperty("token").GetString() ?? throw new InvalidOperationException("Missing antiforgery token.");
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path,
        object? body = null, string? token = null)
    {
        using HttpRequestMessage request = new(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (token is not null) request.Headers.Add("X-CSRF-TOKEN", token);
        return await client.SendAsync(request);
    }

    private static async Task MigrateAsync(string connectionString, IConfiguration configuration, IModule[] modules)
    {
        await PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(connectionString);
        await using IdentityDbContext identity = new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(connectionString).Options);
        await identity.Database.MigrateAsync();
        await using PlatformDbContext platform = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(connectionString).Options);
        await platform.Database.MigrateAsync();
        ServiceCollection services = new();
        ModuleCatalog catalog = services.AddModules(configuration, modules);
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using ApplicationDbContext application = new(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connectionString).Options,
            provider.GetServices<IApplicationModelContributor>(), moduleCatalog: catalog);
        await application.Database.MigrateAsync();
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        foreach (IModuleMigrationContributor contributor in modules.OfType<IModuleMigrationContributor>())
        foreach (ModuleMigration migration in contributor.Migrations)
        {
            await using NpgsqlCommand command = new(migration.Sql, connection);
            await command.ExecuteNonQueryAsync();
        }
        await InstalledSchemaCatalog.SynchronizeAsync(connectionString, modules.SelectMany(module =>
            module.Descriptor.DataResources.Select(resource => new InstalledDataResource(module.Descriptor.Id, resource))));
    }

    private static async Task AssertRecordCountsAsync(string connectionString, long expected)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("""
            SELECT (SELECT count(*) FROM app.projects),
                   (SELECT count(*) FROM app.documents),
                   (SELECT count(*) FROM identity.federation_connections)
            """, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        for (int column = 0; column < 3; column++) reader.GetInt64(column).ShouldBe(expected);
    }

    private sealed record Mutation(string OperationId, HttpMethod Method, string Path, object? Body = null,
        bool IsPlatform = false, bool IsCreate = false);
}
