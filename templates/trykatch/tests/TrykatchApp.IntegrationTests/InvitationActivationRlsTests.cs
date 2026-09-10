using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TrykatchApp.Identity;
using Trykatch.Modules.Documents;
using TrykatchApp.Infrastructure.Modules;
using TrykatchApp.Infrastructure.Organizations;
using TrykatchApp.Infrastructure.Persistence;
using TrykatchApp.Infrastructure.Projects;
using TrykatchApp.Modules;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;

namespace TrykatchApp.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class InvitationActivationRlsTests
{
    private const string AdministratorEmail = "admin@trykatch.test";
    private const string AdministratorPassword = "Local-only!Administrator-Password-42";
    private const string OwnerEmail = "owner@trykatch.test";
    private const string OwnerPassword = "Local-only!Workspace-Owner-Password-42";

    [TestMethod]
    public async Task FreshOwnerInvitationCanBeAcceptedThroughTheRuntimeRlsRole()
    {
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f").Build();
        await postgres.StartAsync();
        string ownerConnection = postgres.GetConnectionString();
        await ApplyMigrationsAsync(ownerConnection);
        (string organizationConnection, string platformConnection, string identityConnection, string outboxConnection) =
            await CreateRuntimeRolesAsync(ownerConnection);

        await using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(webHost =>
            {
                webHost.UseEnvironment("Development");
                webHost.UseSetting("ConnectionStrings:trykatchdb", organizationConnection);
                webHost.UseSetting("ConnectionStrings:trykatch-organization", organizationConnection);
                webHost.UseSetting("ConnectionStrings:trykatch-platform", platformConnection);
                webHost.UseSetting("ConnectionStrings:trykatch-identity", identityConnection);
                webHost.UseSetting("ConnectionStrings:trykatch-outbox", outboxConnection);
                webHost.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:trykatchdb"] = organizationConnection,
                    ["ConnectionStrings:trykatch-organization"] = organizationConnection,
                    ["ConnectionStrings:trykatch-platform"] = platformConnection,
                    ["ConnectionStrings:trykatch-identity"] = identityConnection,
                    ["ConnectionStrings:trykatch-outbox"] = outboxConnection,
                    ["Bootstrap:PlatformAdminEmail"] = AdministratorEmail,
                    ["Bootstrap:PlatformAdminPassword"] = AdministratorPassword
                }));
            });

        using HttpClient client = CreateClient(factory);
        string antiforgery = await GetAntiforgeryTokenAsync(client);
        HttpResponseMessage login = await PostWithAntiforgeryAsync(client, "/api/v1/auth/login", antiforgery, new
        {
            email = AdministratorEmail,
            password = AdministratorPassword,
            rememberMe = false
        });
        login.StatusCode.ShouldBe(HttpStatusCode.OK);

        antiforgery = await GetAntiforgeryTokenAsync(client);
        HttpResponseMessage provision = await PostWithAntiforgeryAsync(client, "/api/v1/tenants", antiforgery, new
        {
            name = "RLS Workspace",
            slug = "rls-workspace",
            administratorEmail = OwnerEmail
        });
        provision.StatusCode.ShouldBe(HttpStatusCode.Created, await provision.Content.ReadAsStringAsync());
        using JsonDocument provisioned = JsonDocument.Parse(await provision.Content.ReadAsStringAsync());
        string invitationToken = provisioned.RootElement.GetProperty("invitationToken").GetString()
            ?? throw new InvalidOperationException("Tenant provisioning did not return an invitation token.");

        antiforgery = await GetAntiforgeryTokenAsync(client);
        (await PostWithAntiforgeryAsync(client, "/api/v1/auth/logout", antiforgery, new { })).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);

        antiforgery = await GetAntiforgeryTokenAsync(client);
        HttpResponseMessage activation = await PostWithAntiforgeryAsync(client, "/api/v1/invitations/activate", antiforgery, new
        {
            token = invitationToken,
            firstName = "Workspace",
            lastName = "Owner",
            password = OwnerPassword
        });
        activation.StatusCode.ShouldBe(HttpStatusCode.OK, await activation.Content.ReadAsStringAsync());
        HttpResponseMessage session = await client.GetAsync("/api/v1/auth/session");
        session.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument sessionPayload = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        sessionPayload.RootElement.GetProperty("displayName").GetString().ShouldBe("Workspace Owner");
        (await client.GetAsync("/api/v1/access")).StatusCode.ShouldBe(HttpStatusCode.OK);

        string invitedEmail = $"new-member-{Guid.NewGuid():N}@trykatch.test";
        antiforgery = await GetAntiforgeryTokenAsync(client);
        HttpResponseMessage invitation = await PostWithAntiforgeryAsync(client, "/api/v1/invitations", antiforgery, new
        {
            email = invitedEmail,
            expiresInDays = 7
        });
        invitation.StatusCode.ShouldBe(HttpStatusCode.OK, await invitation.Content.ReadAsStringAsync());
        using JsonDocument invitationPayload = JsonDocument.Parse(await invitation.Content.ReadAsStringAsync());
        invitationPayload.RootElement.GetProperty("invitationUrl").GetString()
            .ShouldStartWith("https://localhost/invite/");
        invitationPayload.RootElement.GetProperty("emailDelivered").GetBoolean().ShouldBeFalse();
        invitationPayload.RootElement.TryGetProperty("token", out _).ShouldBeFalse();
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory) => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        using JsonDocument payload = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/antiforgery"));
        return payload.RootElement.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("Antiforgery token was not returned.");
    }

    private static Task<HttpResponseMessage> PostWithAntiforgeryAsync(HttpClient client, string url, string token, object body)
    {
        HttpRequestMessage request = new(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", token);
        return client.SendAsync(request);
    }

    private static async Task ApplyMigrationsAsync(string connectionString)
    {
        await using IdentityDbContext identity = new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(connectionString).Options);
        await identity.Database.MigrateAsync();
        await using PlatformDbContext platform = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(connectionString).Options);
        await platform.Database.MigrateAsync();
        await using ApplicationDbContext application = new(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connectionString).Options,
            [new ProjectsModelContributor(), new DocumentsModelContributor()],
            moduleCatalog: new TrykatchModuleCatalog([new ProjectsModule(), new DocumentsModule()]));
        await application.Database.MigrateAsync();
        DocumentsModule documents = new();
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        foreach (TrykatchModuleMigration migration in documents.Migrations)
        {
            await using NpgsqlCommand command = new(migration.Sql, connection);
            await command.ExecuteNonQueryAsync();
        }
        TrykatchInstalledDataResource[] installed = new ProjectsModule().Descriptor.DataResources
            .Select(resource => new TrykatchInstalledDataResource("projects", resource))
            .Concat(documents.Descriptor.DataResources.Select(resource => new TrykatchInstalledDataResource("documents", resource)))
            .ToArray();
        await InstalledSchemaCatalog.SynchronizeAsync(connectionString, installed);
    }

    private static async Task<(string Organization, string Platform, string Identity, string Outbox)> CreateRuntimeRolesAsync(string ownerConnection)
    {
        const string organizationRole = "trykatch_org_runtime";
        const string organizationPassword = "organization-runtime-test-password";
        const string platformRole = "trykatch_platform_runtime";
        const string platformPassword = "platform-runtime-test-password";
        const string identityRole = "trykatch_identity_runtime";
        const string identityPassword = "identity-runtime-test-password";
        const string outboxRole = "trykatch_outbox_worker";
        const string outboxPassword = "outbox-runtime-test-password";
        await using NpgsqlConnection connection = new(ownerConnection);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE ROLE {organizationRole} LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '{organizationPassword}';
            CREATE ROLE {platformRole} LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '{platformPassword}';
            CREATE ROLE {identityRole} LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '{identityPassword}';
            CREATE ROLE {outboxRole} LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '{outboxPassword}';
            REVOKE ALL ON SCHEMA public FROM PUBLIC;
            REVOKE ALL ON ALL TABLES IN SCHEMA public FROM PUBLIC;
            REVOKE ALL ON ALL FUNCTIONS IN SCHEMA public FROM PUBLIC;
            REVOKE TEMPORARY ON DATABASE {QuoteIdentifier(connection.Database)} FROM PUBLIC;
            GRANT CONNECT ON DATABASE {QuoteIdentifier(connection.Database)} TO {organizationRole}, {platformRole}, {identityRole}, {outboxRole};

            GRANT USAGE ON SCHEMA app, platform TO {organizationRole};
            GRANT SELECT ON platform.organizations, platform.module_data_resources TO {organizationRole};
            GRANT SELECT, INSERT, UPDATE, DELETE ON platform.memberships, platform.roles,
                platform.membership_roles, platform.role_permissions, platform.invitations TO {organizationRole};
            GRANT SELECT, INSERT ON platform.audit_entries TO {organizationRole};
            GRANT INSERT ON platform.outbox_messages TO {organizationRole};
            GRANT SELECT, INSERT, UPDATE, DELETE ON app.projects, app.documents TO {organizationRole};

            GRANT USAGE ON SCHEMA platform TO {platformRole};
            GRANT SELECT, INSERT, UPDATE, DELETE ON platform.organizations, platform.organization_data_placements TO {platformRole};
            GRANT SELECT, INSERT ON platform.roles, platform.role_permissions, platform.invitations TO {platformRole};

            GRANT USAGE ON SCHEMA identity TO {identityRole};
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA identity TO {identityRole};
            GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA identity TO {identityRole};

            GRANT USAGE ON SCHEMA platform TO {outboxRole};
            GRANT SELECT, UPDATE ON platform.outbox_messages TO {outboxRole};
            """;
        await command.ExecuteNonQueryAsync();

        string organizationConnection = new NpgsqlConnectionStringBuilder(ownerConnection)
        {
            Username = organizationRole,
            Password = organizationPassword
        }.ConnectionString;
        string platformConnection = new NpgsqlConnectionStringBuilder(ownerConnection)
        {
            Username = platformRole,
            Password = platformPassword
        }.ConnectionString;
        string identityConnection = new NpgsqlConnectionStringBuilder(ownerConnection)
        {
            Username = identityRole,
            Password = identityPassword
        }.ConnectionString;
        string outboxConnection = new NpgsqlConnectionStringBuilder(ownerConnection)
        {
            Username = outboxRole,
            Password = outboxPassword
        }.ConnectionString;
        return (organizationConnection, platformConnection, identityConnection, outboxConnection);
    }

    private static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
