using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Shouldly;

namespace Trykatch.IntegrationTests;

public sealed partial class AccessManagementBoundaryTests
{
    private const string ProjectsWrites = "projects.writes";

    [TestMethod]
    public async Task ProjectsFeatureReadsAndWritesAreIndependentAndDoNotGrantPermissions()
    {
        await using AccessHost host = await AccessHost.StartAsync();
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient owner = await host.CreateOrganizationOwnerAsync(administrator);
        Guid organizationId = (await owner.GetFromJsonAsync<JsonElement>("/api/v1/workspace/current"))
            .GetProperty("organizationId").GetGuid();

        await SaveProjectsEnvironmentAsync(administrator, ProjectReads, enabled: true);
        await SaveProjectsEnvironmentAsync(administrator, ProjectsWrites, enabled: true);
        JsonElement created = await AccessHost.SuccessAsync(owner, HttpMethod.Post, "/api/v1/projects", new
        {
            name = "Feature boundary project", description = "Existing read and write behavior"
        });
        Guid projectId = created.GetProperty("id").GetGuid();
        (await owner.GetFromJsonAsync<JsonElement>($"/api/v1/projects/{projectId}")).GetProperty("id").GetGuid().ShouldBe(projectId);
        (await owner.GetFromJsonAsync<JsonElement>("/api/v1/projects")).GetProperty("items").EnumerateArray()
            .ShouldContain(item => item.GetProperty("id").GetGuid() == projectId);

        Guid viewerRoleId = (await owner.GetFromJsonAsync<JsonElement>("/api/v1/roles")).EnumerateArray()
            .Single(role => role.GetProperty("name").GetString() == "Viewer").GetProperty("id").GetGuid();
        using HttpClient member = await host.InviteAndActivateAsync(owner, "projects-reader@trykatch.test", viewerRoleId);
        (await member.GetAsync($"/api/v1/projects/{projectId}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        ProjectFeatureSnapshot beforePermissionDenial = await ReadProjectsFeatureSnapshotAsync(host.OwnerConnection, organizationId);
        using (HttpResponseMessage deniedForPermission = await AccessHost.SendAsync(member, HttpMethod.Post, "/api/v1/projects", new
               {
                   name = "Permission must still apply", description = "Enabled feature does not grant access"
               }))
            deniedForPermission.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadProjectsFeatureSnapshotAsync(host.OwnerConnection, organizationId)).ShouldBe(beforePermissionDenial);

        await SaveProjectsEnvironmentAsync(administrator, ProjectReads, enabled: false);
        using (HttpResponseMessage detailDisabled = await owner.GetAsync($"/api/v1/projects/{projectId}"))
            await AccessHost.AssertProblemAsync(detailDisabled, HttpStatusCode.Conflict, "feature_unavailable");
        using (HttpResponseMessage listDisabled = await owner.GetAsync("/api/v1/projects"))
            await AccessHost.AssertProblemAsync(listDisabled, HttpStatusCode.Conflict, "feature_unavailable");

        await SaveProjectsEnvironmentAsync(administrator, ProjectReads, enabled: true);
        await SaveProjectsEnvironmentAsync(administrator, ProjectsWrites, enabled: false);
        (await owner.GetFromJsonAsync<JsonElement>($"/api/v1/projects/{projectId}")).GetProperty("id").GetGuid().ShouldBe(projectId);
        (await owner.GetFromJsonAsync<JsonElement>("/api/v1/projects")).GetProperty("items").EnumerateArray()
            .ShouldContain(item => item.GetProperty("id").GetGuid() == projectId);

        ProjectFeatureSnapshot beforeDisabledWrites = await ReadProjectsFeatureSnapshotAsync(host.OwnerConnection, organizationId);
        await AssertProjectFeatureUnavailableAsync(owner, HttpMethod.Post, "/api/v1/projects", new
        {
            name = "Writes disabled", description = "Create must be blocked"
        });
        await AssertProjectFeatureUnavailableAsync(owner, HttpMethod.Put, $"/api/v1/projects/{projectId}", new
        {
            id = projectId, name = "Writes disabled", description = "Update must be blocked"
        });
        await AssertProjectFeatureUnavailableAsync(owner, HttpMethod.Post, $"/api/v1/projects/{projectId}/archive", new { });
        await AssertProjectFeatureUnavailableAsync(owner, HttpMethod.Post, $"/api/v1/projects/{projectId}/restore", new { });
        await AssertProjectFeatureUnavailableAsync(owner, HttpMethod.Delete, $"/api/v1/projects/{projectId}", new
        {
            reason = "Writes disabled"
        });

        using (HttpResponseMessage missingCsrf = await owner.PostAsJsonAsync("/api/v1/projects", new
               {
                   name = "Missing CSRF", description = "Antiforgery still applies while disabled"
               }))
            missingCsrf.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProjectsFeatureSnapshotAsync(host.OwnerConnection, organizationId)).ShouldBe(beforeDisabledWrites);
    }

    [TestMethod]
    public async Task ProjectsFeatureDisabledIdempotentRetryCanBeReplayedAfterReenableWithoutEffects()
    {
        await using AccessHost host = await AccessHost.StartAsync();
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient owner = await host.CreateOrganizationOwnerAsync(administrator);
        Guid organizationId = (await owner.GetFromJsonAsync<JsonElement>("/api/v1/workspace/current"))
            .GetProperty("organizationId").GetGuid();
        await SaveProjectsEnvironmentAsync(administrator, ProjectReads, enabled: true);
        await SaveProjectsEnvironmentAsync(administrator, ProjectsWrites, enabled: true);

        const string key = "projects-feature-retry-01";
        const string body = "{\"name\":\"Feature retry project\",\"description\":\"Same accepted request\"}";
        using HttpResponseMessage first = await SendProjectsFeatureMutationAsync(owner, HttpMethod.Post, "/api/v1/projects/", body, key);
        first.StatusCode.ShouldBe(HttpStatusCode.Created, await first.Content.ReadAsStringAsync());
        byte[] firstBody = await first.Content.ReadAsByteArrayAsync();
        string firstContentType = first.Content.Headers.ContentType!.ToString();
        string firstLocation = first.Headers.Location!.OriginalString;
        ProjectFeatureSnapshot committed = await ReadProjectsFeatureSnapshotAsync(host.OwnerConnection, organizationId);
        committed.ShouldBe(new ProjectFeatureSnapshot(1, 1, 2, 1));

        await SaveProjectsEnvironmentAsync(administrator, ProjectsWrites, enabled: false);
        using (HttpResponseMessage missingCsrf = await owner.PostAsJsonAsync("/api/v1/projects/", new
               {
                   name = "Feature retry project", description = "Same accepted request"
               }))
            missingCsrf.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using (HttpResponseMessage blockedRetry = await SendProjectsFeatureMutationAsync(owner, HttpMethod.Post, "/api/v1/projects/", body, key))
            await AccessHost.AssertProblemAsync(blockedRetry, HttpStatusCode.Conflict, "feature_unavailable");
        (await ReadProjectsFeatureSnapshotAsync(host.OwnerConnection, organizationId)).ShouldBe(committed);

        await SaveProjectsEnvironmentAsync(administrator, ProjectsWrites, enabled: true);
        using HttpResponseMessage replay = await SendProjectsFeatureMutationAsync(owner, HttpMethod.Post, "/api/v1/projects/", body, key);
        replay.StatusCode.ShouldBe(HttpStatusCode.Created);
        replay.Content.Headers.ContentType!.ToString().ShouldBe(firstContentType);
        replay.Headers.Location!.OriginalString.ShouldBe(firstLocation);
        (await replay.Content.ReadAsByteArrayAsync()).SequenceEqual(firstBody).ShouldBeTrue();
        (await ReadProjectsFeatureSnapshotAsync(host.OwnerConnection, organizationId)).ShouldBe(committed);
    }

    private static async Task SaveProjectsEnvironmentAsync(HttpClient administrator, string featureKey, bool enabled)
    {
        JsonElement current = await GetFeatureAdministrationAsync(administrator);
        Guid version = EnvironmentControl(current, featureKey).GetProperty("version").GetGuid();
        using HttpResponseMessage saved = await AccessHost.SendAsync(administrator, HttpMethod.Put,
            $"/api/v1/platform/features/{featureKey}/environment",
            new { defaultEnabled = enabled, forceDisabled = false, expectedVersion = version });
        saved.StatusCode.ShouldBe(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
    }

    private static async Task AssertProjectFeatureUnavailableAsync(HttpClient owner, HttpMethod method, string path, object body)
    {
        using HttpResponseMessage response = await AccessHost.SendAsync(owner, method, path, body);
        await AccessHost.AssertProblemAsync(response, HttpStatusCode.Conflict, "feature_unavailable");
    }

    private static async Task<HttpResponseMessage> SendProjectsFeatureMutationAsync(
        HttpClient owner, HttpMethod method, string path, string body, string idempotencyKey)
    {
        JsonElement csrf = await owner.GetFromJsonAsync<JsonElement>("/api/v1/auth/antiforgery");
        using HttpRequestMessage request = new(method, path) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await owner.SendAsync(request);
    }

    private static async Task<ProjectFeatureSnapshot> ReadProjectsFeatureSnapshotAsync(string connectionString, Guid organizationId)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        long projects = await ProjectsFeatureScalarAsync(connection,
            "SELECT count(*) FROM app.projects WHERE \"OrganizationId\" = @organization", organizationId);
        long audits = await ProjectsFeatureScalarAsync(connection,
            "SELECT count(*) FROM platform.audit_entries WHERE \"OrganizationId\" = @organization AND \"Action\" LIKE 'project.%'", organizationId);
        long outbox = await ProjectsFeatureScalarAsync(connection, """
            SELECT count(*) FROM platform.outbox_messages
            WHERE ("Type" = 'TrykatchApp.Application.Projects.ProjectChanged'
                   AND "Payload"->>'organizationId' = @organization_text)
               OR ("Type" = 'projects.created'
                   AND "ContextJson"->>'organizationId' = @organization_text)
            """, organizationId, organizationId.ToString());
        long receipts = await ProjectsFeatureScalarAsync(connection,
            "SELECT count(*) FROM app.mutation_receipts WHERE \"OrganizationId\" = @organization", organizationId);
        return new(projects, audits, outbox, receipts);
    }

    private static async Task<long> ProjectsFeatureScalarAsync(
        NpgsqlConnection connection, string sql, Guid organizationId, string? organizationText = null)
    {
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("organization", organizationId);
        if (organizationText is not null) command.Parameters.AddWithValue("organization_text", organizationText);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record ProjectFeatureSnapshot(long Projects, long Audits, long Outbox, long Receipts);
}
