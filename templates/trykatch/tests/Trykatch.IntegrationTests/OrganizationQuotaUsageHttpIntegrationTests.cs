using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Trykatch.Infrastructure.Quotas;
using Trykatch.Infrastructure.Persistence;

namespace Trykatch.IntegrationTests;

public sealed partial class AccessManagementBoundaryTests
{
    private const string QuotaUsageEndpoint = "/api/v1/quotas/usage";
    private static readonly string[] SafeUsageResourceFields =
    [
        "committed", "installed", "limit", "mode", "name", "reconciliationHeld", "reserved", "resourceKey", "source", "state", "unit", "version"
    ];

    [TestMethod]
    [DataRow("organizations.read")]
    [DataRow("organizations.manage")]
    public async Task QuotaUsageHttpAllowsEachOrganizationPermissionAndSerializesInt64AsDecimalString(string permission)
    {
        await using AccessHost host = await AccessHost.StartAsync();
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient owner = await host.CreateOrganizationOwnerAsync(administrator);
        Guid organizationId = (await owner.GetFromJsonAsync<JsonElement>("/api/v1/access")).GetProperty("organizationId").GetGuid();
        Guid version = Guid.NewGuid();
        await SeedQuotaUsageAsync(host.OwnerConnection, organizationId, version, long.MaxValue);
        using HttpClient reader = await CreateSinglePermissionMemberAsync(host, owner, permission, $"{permission.Split('.')[1]}@trykatch.test");

        using HttpResponseMessage response = await reader.GetAsync(QuotaUsageEndpoint);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        JsonElement document = await response.Content.ReadFromJsonAsync<JsonElement>();
        document.EnumerateObject().Select(property => property.Name).ShouldBe(["resources"]);
        JsonElement resource = document.GetProperty("resources").EnumerateArray().Single();
        AssertSafeUsageResource(resource, organizationId, version);
        resource.GetProperty("limit").GetString().ShouldBe("9223372036854775807");
        resource.GetProperty("committed").GetString().ShouldBe("9223372036854775807");
    }

    [TestMethod]
    public async Task QuotaUsageHttpDeniesOrganizationMemberWithoutReadOrManagePermission()
    {
        await using AccessHost host = await AccessHost.StartAsync();
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient owner = await host.CreateOrganizationOwnerAsync(administrator);
        using HttpClient member = await host.InviteAndActivateAsync(owner, "quota-usage-denied@trykatch.test");

        using HttpResponseMessage response = await member.GetAsync(QuotaUsageEndpoint);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task QuotaUsageHttpPermissionRejectionPrecedesOrganizationRequestCounting()
    {
        await using AccessHost setup = await AccessHost.StartAsync();
        using HttpClient administrator = await setup.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient owner = await setup.CreateOrganizationOwnerAsync(administrator);
        using HttpClient member = await setup.InviteAndActivateAsync(owner, "usage-limit-denied@trykatch.test");
        Guid organizationId = (await owner.GetFromJsonAsync<JsonElement>("/api/v1/access")).GetProperty("organizationId").GetGuid();
        await using AccessHost replica = await setup.CreateReplicaAsync(RequestLimitSettings(1, 60));
        using HttpClient limitedMember = await replica.SignInAsync("usage-limit-denied@trykatch.test");
        using HttpClient limitedOwner = await replica.SignInAsync("owner@trykatch.test");
        await AccessHost.SuccessAsync(limitedMember, HttpMethod.Post, "/api/v1/workspace/select", new { organizationId, remember = false });
        await AccessHost.SuccessAsync(limitedOwner, HttpMethod.Post, "/api/v1/workspace/select", new { organizationId, remember = false });
        using HttpResponseMessage denied = await limitedMember.GetAsync(QuotaUsageEndpoint);
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadAttemptsAsync(setup.OwnerConnection, organizationId)).ShouldBe(0);
        using HttpResponseMessage admitted = await limitedOwner.GetAsync("/api/v1/access");
        admitted.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAttemptsAsync(setup.OwnerConnection, organizationId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task QuotaUsageHttpPublicationOutageReturnsSanitizedUnavailableWithoutInventingCapacity()
    {
        await using AccessHost host = await AccessHost.StartAsync();
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient owner = await host.CreateOrganizationOwnerAsync(administrator);
        IConfiguration configuration = host.Services.GetRequiredService<IConfiguration>();
        string key = $"ConnectionStrings:{RuntimeDatabaseConnectionContract.Platform}";
        string original = configuration[key]!;
        configuration[key] = new NpgsqlConnectionStringBuilder(original) { Port = 1, Timeout = 1 }.ConnectionString;
        try
        {
            using HttpResponseMessage response = await owner.GetAsync(QuotaUsageEndpoint);
            await AccessHost.AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "quota_usage_unavailable");
            string body = await response.Content.ReadAsStringAsync();
            body.ShouldNotContain("Password");
            body.ShouldNotContain("resources");
        }
        finally { configuration[key] = original; }
    }

    [TestMethod]
    public async Task QuotaUsageHttpIgnoresSuppliedOrganizationQueryAndHeaderAsAuthority()
    {
        await using AccessHost host = await AccessHost.StartAsync();
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient owner = await host.CreateOrganizationOwnerAsync(administrator);
        Guid selectedOrganizationId = (await owner.GetFromJsonAsync<JsonElement>("/api/v1/access")).GetProperty("organizationId").GetGuid();
        Guid otherOrganizationId = await CreateAdditionalOrganizationAsync(host, administrator);
        Guid selectedVersion = Guid.NewGuid();
        await SeedQuotaUsageAsync(host.OwnerConnection, selectedOrganizationId, selectedVersion, 11);
        await SeedQuotaUsageAsync(host.OwnerConnection, otherOrganizationId, Guid.NewGuid(), 97);
        using HttpClient reader = await CreateSinglePermissionMemberAsync(host, owner, "organizations.read", "quota-usage-spoof@trykatch.test");
        using HttpRequestMessage request = new(HttpMethod.Get, $"{QuotaUsageEndpoint}?organizationId={otherOrganizationId}");
        request.Headers.Add("X-Organization-Id", otherOrganizationId.ToString());

        using HttpResponseMessage response = await reader.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        JsonElement document = await response.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement resource = document.GetProperty("resources").EnumerateArray().Single();
        AssertSafeUsageResource(resource, selectedOrganizationId, selectedVersion);
        resource.GetProperty("limit").GetString().ShouldBe("11");
        resource.GetProperty("committed").GetString().ShouldBe("11");
        resource.GetProperty("limit").GetString().ShouldNotBe("97");
        resource.GetProperty("committed").GetString().ShouldNotBe("97");
    }

    private static async Task<HttpClient> CreateSinglePermissionMemberAsync(AccessHost host, HttpClient owner, string permission, string email)
    {
        JsonElement role = await AccessHost.SuccessAsync(owner, HttpMethod.Post, "/api/v1/roles", new
        {
            id = (Guid?)null,
            name = $"Quota usage {permission}",
            description = "Read quota usage",
            permissions = new[] { permission }
        });
        HttpClient member = await host.InviteAndActivateAsync(owner, email);
        Guid memberId = (await AccessHost.OrganizationMemberAsync(owner, email)).GetProperty("id").GetGuid();
        await AccessHost.SuccessAsync(owner, HttpMethod.Put, $"/api/v1/members/{memberId}", new
        {
            membershipId = memberId,
            roleIds = new[] { role.GetProperty("id").GetGuid() },
            isActive = true
        });
        return member;
    }

    private static async Task<Guid> CreateAdditionalOrganizationAsync(AccessHost host, HttpClient administrator)
    {
        JsonElement created = await AccessHost.SuccessAsync(administrator, HttpMethod.Post, "/api/v1/tenants", new
        {
            name = "Other quota workspace",
            slug = "other-quota-workspace",
            administratorEmail = "other-quota-owner@trykatch.test"
        });
        using HttpClient otherOwner = await host.ActivateOrganizationInvitationAsync(created.GetProperty("invitationToken").GetString()!);
        return (await otherOwner.GetFromJsonAsync<JsonElement>("/api/v1/access")).GetProperty("organizationId").GetGuid();
    }

    private static async Task SeedQuotaUsageAsync(string ownerConnection, Guid organizationId, Guid version, long units)
    {
        await using NpgsqlConnection connection = new(ownerConnection);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("""
            INSERT INTO app.organization_entitlements
                ("OrganizationId", "ResourceKey", "OwnerModuleId", "Unit", "Mode", "Limit", "Version",
                 "Source", "SourceRevision", "SourceHash", "EffectiveAtUtc")
            VALUES (@organization, 'documents.storage-bytes', 'documents', 1, 1, @units, @version,
                    'host-configuration', 1, @hash, CURRENT_TIMESTAMP);
            INSERT INTO platform.entitlement_publication_history
                ("OrganizationId", "SourceRevision", "SourceHash", "Source", "SourceVersion", "ActorId", "InitialResourceCount", "PublishedAtUtc")
            VALUES (@organization, 1, @hash, 'host-configuration', 1, @publisher, 1, CURRENT_TIMESTAMP);
            INSERT INTO app.organization_quota_inventory
                ("OrganizationId", "ResourceKey", "SourceKey", "SourceVersion", "Cursor", "State", "UpdatedAtUtc")
            VALUES (@organization, 'documents.storage-bytes', 'documents.inventory', 1, NULL, 1, CURRENT_TIMESTAMP);
            INSERT INTO app.organization_usage
                ("OrganizationId", "ResourceKey", "EffectId", "SourceId", "SourceEffectId", "ReservationId", "ActorId", "Units", "OccurredAtUtc")
            VALUES (@organization, 'documents.storage-bytes', @effect, @source, NULL, NULL, @actor, @units, CURRENT_TIMESTAMP);
            """, connection);
        command.Parameters.AddWithValue("organization", organizationId);
        command.Parameters.AddWithValue("units", units);
        command.Parameters.AddWithValue("version", version);
        command.Parameters.AddWithValue("hash", Guid.NewGuid().ToString("N").PadRight(64, 'a'));
        command.Parameters.AddWithValue("publisher", HostEntitlementPublisher.AuditActorId);
        command.Parameters.AddWithValue("effect", Guid.NewGuid());
        command.Parameters.AddWithValue("source", Guid.NewGuid());
        command.Parameters.AddWithValue("actor", Guid.NewGuid());
        await command.ExecuteNonQueryAsync();
    }

    private static void AssertSafeUsageResource(JsonElement resource, Guid organizationId, Guid version)
    {
        resource.EnumerateObject().Select(property => property.Name).OrderBy(name => name).ShouldBe(SafeUsageResourceFields.OrderBy(name => name));
        resource.GetProperty("resourceKey").GetString().ShouldBe("documents.storage-bytes");
        resource.GetProperty("name").GetString().ShouldBe("Document storage");
        resource.GetProperty("unit").GetString().ShouldBe("Bytes");
        resource.GetProperty("mode").GetString().ShouldBe("Finite");
        resource.GetProperty("version").GetGuid().ShouldBe(version);
        resource.GetProperty("source").GetString().ShouldBe("host-configuration");
        resource.GetProperty("reserved").GetString().ShouldBe("0");
        resource.GetProperty("reconciliationHeld").GetString().ShouldBe("0");
        resource.GetProperty("state").GetString().ShouldBe("Ready");
        resource.GetProperty("installed").GetBoolean().ShouldBeTrue();
        resource.GetProperty("resourceKey").GetString()!.ShouldNotContain(organizationId.ToString());
        resource.EnumerateObject().Select(property => property.Name.ToLowerInvariant()).ShouldNotContain(name =>
            name.Contains("actor") || name.Contains("effect") || name.Contains("objectkey") || name.Contains("secret"));
    }
}
