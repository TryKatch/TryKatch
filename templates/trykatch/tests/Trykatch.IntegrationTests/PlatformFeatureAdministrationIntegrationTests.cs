using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using Trykatch.Application.Identity;
using Trykatch.Identity;

namespace Trykatch.IntegrationTests;

public sealed partial class AccessManagementBoundaryTests
{
    private const string ProjectReads = "projects.reads";

    [TestMethod]
    public async Task PlatformFeatureAdministrationUsesDefaultAndExplicitIndependentPermissions()
    {
        await using AccessHost host = await AccessHost.StartAsync();
        using HttpClient anonymous = host.CreateClient();
        (await anonymous.GetAsync("/api/v1/platform/features")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        JsonElement initial = await GetFeatureAdministrationAsync(administrator);
        Guid initialVersion = EnvironmentControl(initial, ProjectReads).GetProperty("version").GetGuid();

        using HttpClient owner = await host.CreateOrganizationOwnerAsync(administrator);
        (await owner.GetAsync("/api/v1/platform/features")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        foreach (string role in new[] { PlatformRoles.Operator, PlatformRoles.Auditor })
        {
            using HttpClient client = await host.GrantAndSignInAsync(administrator,
                $"feature-{role}@trykatch.test", role);
            (await client.GetAsync("/api/v1/platform/features")).StatusCode.ShouldBe(HttpStatusCode.Forbidden, role);
            using HttpResponseMessage deniedWrite = await AccessHost.SendAsync(client, HttpMethod.Put,
                $"/api/v1/platform/features/{ProjectReads}/environment",
                new { defaultEnabled = false, forceDisabled = false, expectedVersion = initialVersion });
            deniedWrite.StatusCode.ShouldBe(HttpStatusCode.Forbidden, role);
        }

        JsonElement readRole = await AccessHost.SuccessAsync(administrator, HttpMethod.Post,
            "/api/v1/platform-users/roles", new
            {
                name = "Feature reader", description = "Explicit read-only feature access",
                permissions = new[] { PlatformPermissions.FeaturesRead }
            });
        using HttpClient reader = await host.GrantAndSignInAsync(administrator,
            "feature-reader@trykatch.test", readRole.GetProperty("key").GetString()!);
        (await reader.GetAsync("/api/v1/platform/features")).StatusCode.ShouldBe(HttpStatusCode.OK);
        using (HttpResponseMessage deniedWrite = await AccessHost.SendAsync(reader, HttpMethod.Put,
                   $"/api/v1/platform/features/{ProjectReads}/environment",
                   new { defaultEnabled = false, forceDisabled = false, expectedVersion = initialVersion }))
            deniedWrite.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        JsonElement manageRole = await AccessHost.SuccessAsync(administrator, HttpMethod.Post,
            "/api/v1/platform-users/roles", new
            {
                name = "Feature manager", description = "Explicit manage-only feature access",
                permissions = new[] { PlatformPermissions.FeaturesManage }
            });
        // Role authoring preserves the existing manage -> read normalization. Seed a narrow stored grant
        // independently to exercise the application's permission boundary, including imported/custom roles.
        await using (IdentityDbContext identity = new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(host.OwnerConnection).Options))
        {
            string roleKey = manageRole.GetProperty("key").GetString()!;
            Guid roleId = await identity.Roles.Where(role => role.Name == roleKey).Select(role => role.Id).SingleAsync();
            await identity.RoleClaims.Where(claim => claim.RoleId == roleId && claim.ClaimType == "platform_permission"
                && claim.ClaimValue == PlatformPermissions.FeaturesRead).ExecuteDeleteAsync();
        }
        using HttpClient manager = await host.GrantAndSignInAsync(administrator,
            "feature-manager@trykatch.test", manageRole.GetProperty("key").GetString()!);
        (await manager.GetAsync("/api/v1/platform/features")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using (HttpResponseMessage saved = await AccessHost.SendAsync(manager, HttpMethod.Put,
                   $"/api/v1/platform/features/{ProjectReads}/environment",
                   new { defaultEnabled = false, forceDisabled = false, expectedVersion = initialVersion }))
            saved.StatusCode.ShouldBe(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());

        using (HttpResponseMessage stale = await AccessHost.SendAsync(manager, HttpMethod.Put,
                   $"/api/v1/platform/features/{ProjectReads}/environment",
                   new { defaultEnabled = true, forceDisabled = false, expectedVersion = initialVersion }))
            await AccessHost.AssertProblemAsync(stale, HttpStatusCode.Conflict, "feature_control_conflict");
        using (HttpResponseMessage unknown = await AccessHost.SendAsync(manager, HttpMethod.Put,
                   "/api/v1/platform/features/unknown.feature/environment",
                   new { defaultEnabled = true, forceDisabled = false, expectedVersion = Guid.Empty }))
            unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        foreach (Guid organization in new[] { Guid.NewGuid(), Guid.Empty })
        {
            using HttpResponseMessage absent = await AccessHost.SendAsync(manager, HttpMethod.Put,
                $"/api/v1/platform/features/{ProjectReads}/organizations/{organization}",
                new { enabled = true, expectedVersion = Guid.Empty });
            absent.StatusCode.ShouldBe(organization == Guid.Empty ? HttpStatusCode.BadRequest : HttpStatusCode.NotFound);
        }
        JsonElement bounded = await administrator.GetFromJsonAsync<JsonElement>(
            $"/api/v1/platform/features/{ProjectReads}/history?page=0&pageSize=1000");
        bounded.GetProperty("page").GetInt32().ShouldBe(1);
        bounded.GetProperty("pageSize").GetInt32().ShouldBe(100);
        bounded.GetProperty("totalCount").GetInt64().ShouldBe(1);

        await AccessHost.SuccessAsync(administrator, HttpMethod.Put,
            $"/api/v1/platform-users/roles/{readRole.GetProperty("key").GetString()}", new
            {
                name = "Feature reader", description = "Feature read access revoked", permissions = Array.Empty<string>()
            });
        (await reader.GetAsync("/api/v1/platform/features")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using HttpClient revokedReader = await host.SignInAsync("feature-reader@trykatch.test");
        (await revokedReader.GetAsync("/api/v1/platform/features")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [TestMethod]
    public async Task OrganizationFeatureSnapshotUsesCurrentWorkspaceAndControlPrecedence()
    {
        await using AccessHost host = await AccessHost.StartAsync(additionalSettings: new Dictionary<string, string?>
        {
            [$"Features:Defaults:{ProjectReads}"] = "false"
        });
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient owner = await host.CreateOrganizationOwnerAsync(administrator);
        Guid organizationId = (await owner.GetFromJsonAsync<JsonElement>("/api/v1/workspace/current"))
            .GetProperty("organizationId").GetGuid();

        JsonElement before = await GetFeatureAdministrationAsync(administrator);
        before.GetProperty("environment").GetString().ShouldBe("development");
        EnvironmentControl(before, ProjectReads).GetProperty("version").GetGuid().ShouldBe(Guid.Empty);
        using (HttpResponseMessage saveEnvironment = await AccessHost.SendAsync(administrator, HttpMethod.Put,
                   $"/api/v1/platform/features/{ProjectReads}/environment",
                   new { defaultEnabled = false, forceDisabled = false, expectedVersion = Guid.Empty }))
            saveEnvironment.StatusCode.ShouldBe(HttpStatusCode.OK, await saveEnvironment.Content.ReadAsStringAsync());

        JsonElement noOverride = await FeatureSnapshotAsync(owner);
        noOverride.GetProperty("organizationId").GetGuid().ShouldBe(organizationId);
        noOverride.GetProperty("values").GetProperty(ProjectReads).GetBoolean().ShouldBeFalse();

        JsonElement withOrganization = await GetFeatureAdministrationAsync(administrator, organizationId);
        Guid organizationVersion = OrganizationControl(withOrganization, ProjectReads).GetProperty("version").GetGuid();
        using (HttpResponseMessage saveOrganization = await AccessHost.SendAsync(administrator, HttpMethod.Put,
                   $"/api/v1/platform/features/{ProjectReads}/organizations/{organizationId}",
                   new { enabled = true, expectedVersion = organizationVersion }))
            saveOrganization.StatusCode.ShouldBe(HttpStatusCode.OK, await saveOrganization.Content.ReadAsStringAsync());

        using (HttpResponseMessage currentWithForeignSelectors = await owner.GetAsync(
                   $"/api/v1/features?organizationId={Guid.NewGuid()}&environment=staging"))
        {
            currentWithForeignSelectors.StatusCode.ShouldBe(HttpStatusCode.OK, await currentWithForeignSelectors.Content.ReadAsStringAsync());
            JsonElement current = await currentWithForeignSelectors.Content.ReadFromJsonAsync<JsonElement>();
            current.GetProperty("organizationId").GetGuid().ShouldBe(organizationId);
            current.GetProperty("values").GetProperty(ProjectReads).GetBoolean().ShouldBeTrue();
            string snapshotJson = current.GetRawText();
            foreach (string privateField in new[] { "actorId", "history", "version", "control", "forceDisabled" })
                snapshotJson.ShouldNotContain(privateField, Case.Sensitive);
        }

        JsonElement environmentAfterOrg = await GetFeatureAdministrationAsync(administrator, organizationId,
            "?environment=staging");
        environmentAfterOrg.GetProperty("environment").GetString().ShouldBe("development");
        JsonElement environmentControl = EnvironmentControl(environmentAfterOrg, ProjectReads);
        Guid environmentVersion = environmentControl.GetProperty("version").GetGuid();
        using (HttpResponseMessage forceOff = await AccessHost.SendAsync(administrator, HttpMethod.Put,
                   $"/api/v1/platform/features/{ProjectReads}/environment",
                   new { defaultEnabled = true, forceDisabled = true, expectedVersion = environmentVersion }))
            forceOff.StatusCode.ShouldBe(HttpStatusCode.OK, await forceOff.Content.ReadAsStringAsync());
        (await FeatureSnapshotAsync(owner)).GetProperty("values").GetProperty(ProjectReads).GetBoolean().ShouldBeFalse();

        JsonElement beforeReset = await GetFeatureAdministrationAsync(administrator, organizationId);
        Guid beforeResetVersion = OrganizationControl(beforeReset, ProjectReads).GetProperty("version").GetGuid();
        using (HttpResponseMessage reset = await AccessHost.SendAsync(administrator, HttpMethod.Put,
                   $"/api/v1/platform/features/{ProjectReads}/organizations/{organizationId}",
                   new { enabled = (bool?)null, expectedVersion = beforeResetVersion }))
            reset.StatusCode.ShouldBe(HttpStatusCode.OK, await reset.Content.ReadAsStringAsync());

        JsonElement afterReset = await GetFeatureAdministrationAsync(administrator, organizationId);
        JsonElement resetControl = OrganizationControl(afterReset, ProjectReads);
        resetControl.GetProperty("enabled").ValueKind.ShouldBe(JsonValueKind.Null);
        resetControl.GetProperty("version").GetGuid().ShouldNotBe(beforeResetVersion);
        using HttpResponseMessage historyResponse = await administrator.GetAsync(
            $"/api/v1/platform/features/{ProjectReads}/history?organizationId={organizationId}&page=1&pageSize=10");
        historyResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await historyResponse.Content.ReadAsStringAsync());
        JsonElement history = await historyResponse.Content.ReadFromJsonAsync<JsonElement>();
        history.GetProperty("totalCount").GetInt64().ShouldBe(2);
        history.GetProperty("items").EnumerateArray().ShouldContain(entry =>
            entry.GetProperty("enabled").ValueKind == JsonValueKind.Null
            && entry.GetProperty("version").GetGuid() == resetControl.GetProperty("version").GetGuid());
    }

    [TestMethod]
    public async Task PlatformFeatureSaveRequiresCsrfAndRollsBackControlWhenHistoryInsertFails()
    {
        await using AccessHost host = await AccessHost.StartAsync();
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        JsonElement initial = await GetFeatureAdministrationAsync(administrator);
        Guid expectedVersion = EnvironmentControl(initial, ProjectReads).GetProperty("version").GetGuid();

        using (HttpResponseMessage noCsrf = await administrator.PutAsJsonAsync(
                   $"/api/v1/platform/features/{ProjectReads}/environment",
                   new { defaultEnabled = false, forceDisabled = false, expectedVersion }))
            noCsrf.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        await InstallFailingHistoryTriggerAsync(host.OwnerConnection);
        try
        {
            using HttpResponseMessage failed = await AccessHost.SendAsync(administrator, HttpMethod.Put,
                $"/api/v1/platform/features/{ProjectReads}/environment",
                new { defaultEnabled = false, forceDisabled = false, expectedVersion });
            failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError, await failed.Content.ReadAsStringAsync());
        }
        finally
        {
            await RemoveFailingHistoryTriggerAsync(host.OwnerConnection);
        }

        JsonElement afterFailure = await GetFeatureAdministrationAsync(administrator);
        JsonElement unchanged = EnvironmentControl(afterFailure, ProjectReads);
        unchanged.GetProperty("version").GetGuid().ShouldBe(expectedVersion);
        unchanged.GetProperty("enabled").ValueKind.ShouldBe(JsonValueKind.Null);
        using (HttpResponseMessage historyResponse = await administrator.GetAsync(
                   $"/api/v1/platform/features/{ProjectReads}/history?page=1&pageSize=10"))
        {
            historyResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
            JsonElement history = await historyResponse.Content.ReadFromJsonAsync<JsonElement>();
            history.GetProperty("totalCount").GetInt64().ShouldBe(0);
        }
    }

    private static async Task<JsonElement> GetFeatureAdministrationAsync(HttpClient client, Guid? organizationId = null,
        string extraQuery = "")
    {
        string path = "/api/v1/platform/features";
        if (organizationId is Guid id) path += $"?organizationId={id}";
        else if (!string.IsNullOrEmpty(extraQuery)) path += "?" + extraQuery.TrimStart('?');
        if (organizationId is not null && !string.IsNullOrEmpty(extraQuery)) path += "&" + extraQuery.TrimStart('?');
        using HttpResponseMessage response = await client.GetAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }

    private static async Task<JsonElement> FeatureSnapshotAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync("/api/v1/features");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }

    private static JsonElement EnvironmentControl(JsonElement snapshot, string featureKey) =>
        snapshot.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("declaration").GetProperty("key").GetString() == featureKey)
            .GetProperty("environmentControl");

    private static JsonElement OrganizationControl(JsonElement snapshot, string featureKey)
    {
        JsonElement control = snapshot.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("declaration").GetProperty("key").GetString() == featureKey)
            .GetProperty("organizationControl");
        control.ValueKind.ShouldNotBe(JsonValueKind.Null);
        return control;
    }

    private static async Task InstallFailingHistoryTriggerAsync(string ownerConnection)
    {
        await using NpgsqlConnection connection = new(ownerConnection);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("""
            CREATE FUNCTION platform.fail_test_feature_history() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              RAISE EXCEPTION 'Injected feature history persistence failure';
            END $$;
            CREATE TRIGGER fail_test_feature_history BEFORE INSERT ON platform.feature_control_history
              FOR EACH ROW EXECUTE FUNCTION platform.fail_test_feature_history();
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RemoveFailingHistoryTriggerAsync(string ownerConnection)
    {
        await using NpgsqlConnection connection = new(ownerConnection);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("""
            DROP TRIGGER fail_test_feature_history ON platform.feature_control_history;
            DROP FUNCTION platform.fail_test_feature_history();
            """, connection);
        await command.ExecuteNonQueryAsync();
    }
}
