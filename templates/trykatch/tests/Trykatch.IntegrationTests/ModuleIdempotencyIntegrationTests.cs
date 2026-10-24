using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Shouldly;
using static Trykatch.IntegrationTests.AccountSecurityStepUpTests;

namespace Trykatch.IntegrationTests;

[TestClass, TestCategory("Integration"), DoNotParallelize]
public sealed class ModuleIdempotencyIntegrationTests
{
    private static SecurityHost host = null!;
    private static HttpClient browser = null!;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext _)
    {
        host = await SecurityHost.StartAsync();
    }

    [TestInitialize]
    public async Task StartIndependentHttpSessionAsync()
    {
        browser?.Dispose();
        // Each case gets the real production limiter with its own window, rather than
        // exhausting one shared fixture's antiforgery quota across unrelated scenarios.
        await host.RestartAsync(new Dictionary<string, string?>(), "Development");
        browser = await host.SignInAsync();
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        browser?.Dispose();
        if (host is not null) await host.DisposeAsync();
    }

    [TestMethod]
    public async Task ProjectsCreateWithoutAnIdempotencyKeyKeepsItsExistingHttpBehavior()
    {
        Guid organizationId = await CreateWorkspaceAsync();
        ProjectSnapshot before = await ReadProjectSnapshotAsync(organizationId);

        using HttpResponseMessage response = await SendMutationAsync(HttpMethod.Post, "/api/v1/projects/",
            ProjectJson("No key project", "Existing behavior"));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        response.Headers.Location.ShouldNotBeNull();
        ProjectSnapshot after = await ReadProjectSnapshotAsync(organizationId);
        after.Projects.ShouldBe(before.Projects + 1);
        after.Audits.ShouldBe(before.Audits + 1);
        after.Outbox.ShouldBe(before.Outbox + 2);
        after.Receipts.ShouldBe(before.Receipts);
    }

    [TestMethod]
    public async Task ProjectsCreateRejectsMalformedDuplicateAndOversizedKeysWithoutWrites()
    {
        Guid organizationId = await CreateWorkspaceAsync();
        ProjectSnapshot before = await ReadProjectSnapshotAsync(organizationId);
        string token = await GetCsrfTokenAsync();

        using (HttpRequestMessage malformed = MutationRequest(HttpMethod.Post, "/api/v1/projects/",
                   ProjectJson("Malformed key", "No writes"), token))
        {
            malformed.Headers.TryAddWithoutValidation("Idempotency-Key", "invalid key").ShouldBeTrue();
            using HttpResponseMessage response = await browser.SendAsync(malformed);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (HttpRequestMessage duplicate = MutationRequest(HttpMethod.Post, "/api/v1/projects/",
                   ProjectJson("Duplicate key", "No writes"), token))
        {
            duplicate.Headers.TryAddWithoutValidation("Idempotency-Key", ["duplicate-a", "duplicate-b"]).ShouldBeTrue();
            using HttpResponseMessage response = await browser.SendAsync(duplicate);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (HttpRequestMessage oversized = MutationRequest(HttpMethod.Post, "/api/v1/projects/",
                   ProjectJson("Oversized key", "No writes"), token))
        {
            oversized.Headers.TryAddWithoutValidation("Idempotency-Key", new string('k', 129)).ShouldBeTrue();
            using HttpResponseMessage response = await browser.SendAsync(oversized);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (HttpRequestMessage oversizedBody = MutationRequest(HttpMethod.Post, "/api/v1/projects/",
                   new string(' ', 65_537), token, "oversized-body"))
        {
            using HttpResponseMessage response = await browser.SendAsync(oversizedBody);
            response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        }

        (await ReadProjectSnapshotAsync(organizationId)).ShouldBe(before);
    }

    [TestMethod]
    public async Task CaseAliasedJsonPropertiesCannotExecuteOrPoisonTheRequestIdentity()
    {
        Guid organizationId = await CreateWorkspaceAsync();
        const string key = "case-alias-01";
        const string ambiguous = "{\"name\":\"First intent\",\"Name\":\"Second intent\",\"description\":\"ambiguous\"}";
        using HttpResponseMessage rejected = await SendMutationAsync(HttpMethod.Post, "/api/v1/projects/", ambiguous, key);
        rejected.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        rejected.Headers.Location.ShouldBeNull();
        (await ReadProjectSnapshotAsync(organizationId)).ShouldBe(new ProjectSnapshot(0, 0, 0, 0));

        const string valid = "{\"name\":\"Unambiguous intent\",\"description\":\"corrected\"}";
        using HttpResponseMessage accepted = await SendMutationAsync(HttpMethod.Post, "/api/v1/projects/", valid, key);
        accepted.StatusCode.ShouldBe(HttpStatusCode.Created);
        byte[] original = await accepted.Content.ReadAsByteArrayAsync();
        using HttpResponseMessage replay = await SendMutationAsync(HttpMethod.Post, "/api/v1/projects/", valid, key);
        replay.StatusCode.ShouldBe(HttpStatusCode.Created);
        replay.Headers.Location!.OriginalString.ShouldBe(accepted.Headers.Location!.OriginalString);
        (await replay.Content.ReadAsByteArrayAsync()).ShouldBe(original);
        (await ReadProjectSnapshotAsync(organizationId)).ShouldBe(new ProjectSnapshot(1, 1, 2, 1));
    }

    [TestMethod]
    public async Task ProjectsCreateReplaysCanonicalJsonWithTheExactReceiptAndSingleEffect()
    {
        Guid organizationId = await CreateWorkspaceAsync();
        const string key = "canonical-retry-01";
        using HttpResponseMessage first = await SendMutationAsync(HttpMethod.Post, "/api/v1/projects/",
            "{\"name\":\"Canonical project\",\"description\":\"same intent\"}", key);
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        byte[] firstBody = await first.Content.ReadAsByteArrayAsync();
        string firstContentType = first.Content.Headers.ContentType!.ToString();
        string firstLocation = first.Headers.Location!.OriginalString;

        using HttpResponseMessage replay = await SendMutationAsync(HttpMethod.Post, "/api/v1/projects/",
            "{ \"description\" : \"same intent\", \"name\" : \"Canonical project\" }", key);

        replay.StatusCode.ShouldBe(HttpStatusCode.Created);
        replay.Content.Headers.ContentType!.ToString().ShouldBe(firstContentType);
        replay.Headers.Location!.OriginalString.ShouldBe(firstLocation);
        (await replay.Content.ReadAsByteArrayAsync()).SequenceEqual(firstBody).ShouldBeTrue();
        using JsonDocument receipt = JsonDocument.Parse(firstBody);
        Guid projectId = receipt.RootElement.GetProperty("id").GetGuid();
        ProjectSnapshot snapshot = await ReadProjectSnapshotAsync(organizationId);
        snapshot.Projects.ShouldBe(1);
        snapshot.Audits.ShouldBe(1);
        snapshot.Outbox.ShouldBe(2);
        snapshot.Receipts.ShouldBe(1);
        (await ReadStoredKeyHashAsync(organizationId)).ShouldBe(Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(key))));
        firstLocation.ShouldBe($"/api/v1/projects/{projectId}");
    }

    [TestMethod]
    public async Task ConcurrentProjectsCreateRetryReturnsOneCommittedReceipt()
    {
        Guid organizationId = await CreateWorkspaceAsync();
        string token = await GetCsrfTokenAsync();
        const string key = "concurrent-create-01";
        const string body = "{\"name\":\"Concurrent project\",\"description\":\"one effect\"}";
        using HttpRequestMessage one = MutationRequest(HttpMethod.Post, "/api/v1/projects/", body, token, key);
        using HttpRequestMessage two = MutationRequest(HttpMethod.Post, "/api/v1/projects/", body, token, key);

        HttpResponseMessage[] concurrent = await Task.WhenAll(browser.SendAsync(one), browser.SendAsync(two));
        try
        {
            concurrent.ShouldAllBe(response => response.StatusCode == HttpStatusCode.Created || response.StatusCode == HttpStatusCode.Conflict);
            concurrent.Count(response => response.StatusCode == HttpStatusCode.Created).ShouldBeGreaterThan(0);
            using HttpResponseMessage retry = await SendMutationAsync(HttpMethod.Post, "/api/v1/projects/", body, key);
            retry.StatusCode.ShouldBe(HttpStatusCode.Created);
            byte[] receipt = await retry.Content.ReadAsByteArrayAsync();
            foreach (HttpResponseMessage response in concurrent.Where(item => item.StatusCode == HttpStatusCode.Created))
            {
                response.Headers.Location!.OriginalString.ShouldBe(retry.Headers.Location!.OriginalString);
                (await response.Content.ReadAsByteArrayAsync()).SequenceEqual(receipt).ShouldBeTrue();
            }
        }
        finally
        {
            foreach (HttpResponseMessage response in concurrent) response.Dispose();
        }
        (await ReadProjectSnapshotAsync(organizationId)).ShouldBe(new ProjectSnapshot(1, 1, 2, 1));
    }

    [TestMethod]
    public async Task ProjectsCreateRejectsChangedBodyQueryAndTrailingSlashForTheSameKey()
    {
        Guid organizationId = await CreateWorkspaceAsync();
        const string key = "intent-conflict-01";
        const string original = "{\"name\":\"Conflict source\",\"description\":\"same key\"}";
        using HttpResponseMessage created = await SendMutationAsync(HttpMethod.Post, "/api/v1/projects/", original, key);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);

        using HttpResponseMessage changedBody = await SendMutationAsync(HttpMethod.Post, "/api/v1/projects/",
            "{\"name\":\"Changed body\",\"description\":\"same key\"}", key);
        changedBody.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using HttpResponseMessage changedQuery = await SendMutationAsync(HttpMethod.Post, "/api/v1/projects/?source=retry", original, key);
        changedQuery.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using HttpResponseMessage changedPath = await SendMutationAsync(HttpMethod.Post, "/api/v1/projects", original, key);
        changedPath.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await ReadProjectSnapshotAsync(organizationId)).ShouldBe(new ProjectSnapshot(1, 1, 2, 1));
    }

    [TestMethod]
    public async Task AntiforgeryRejectsReplayBeforeReturningTheStoredReceipt()
    {
        Guid organizationId = await CreateWorkspaceAsync();
        using HttpResponseMessage first = await SendMutationAsync(HttpMethod.Post, "/api/v1/projects/",
            ProjectJson("CSRF receipt", "must not disclose"), "csrf-before-replay-01");
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        string storedBody = await first.Content.ReadAsStringAsync();
        string storedLocation = first.Headers.Location!.OriginalString;

        using HttpResponseMessage missing = await SendMutationAsync(HttpMethod.Post, "/api/v1/projects/",
            ProjectJson("CSRF receipt", "must not disclose"), "csrf-before-replay-01", csrfToken: null, acquireCsrf: false);
        using HttpResponseMessage invalid = await SendMutationAsync(HttpMethod.Post, "/api/v1/projects/",
            ProjectJson("CSRF receipt", "must not disclose"), "csrf-before-replay-01", csrfToken: "tampered-token");

        foreach (HttpResponseMessage response in new[] { missing, invalid })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            response.Headers.Location.ShouldBeNull();
            (await response.Content.ReadAsStringAsync()).ShouldNotContain(storedBody);
            response.Headers.Location?.OriginalString.ShouldNotBe(storedLocation);
        }
        (await ReadProjectSnapshotAsync(organizationId)).ShouldBe(new ProjectSnapshot(1, 1, 2, 1));
    }

    [TestMethod]
    public async Task RevokedManagePermissionDeniesAPreviouslyCompletedReceipt()
    {
        Guid organizationId = await CreateWorkspaceAsync();
        const string key = "revoked-manage-01";
        string body = ProjectJson("Revoked receipt", "no disclosure");
        using HttpResponseMessage first = await SendMutationAsync(HttpMethod.Post, "/api/v1/projects/", body, key);
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        string storedBody = await first.Content.ReadAsStringAsync();
        await RemovePermissionAsync(organizationId, "projects.manage");

        using HttpResponseMessage denied = await SendMutationAsync(HttpMethod.Post, "/api/v1/projects/", body, key);

        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        denied.Headers.Location.ShouldBeNull();
        (await denied.Content.ReadAsStringAsync()).ShouldNotContain(storedBody);
        (await ReadProjectSnapshotAsync(organizationId)).ShouldBe(new ProjectSnapshot(1, 1, 2, 1));
    }

    [TestMethod]
    public async Task InvalidResourceReferenceDeniesReceiptReplay()
    {
        Guid organizationId = await CreateWorkspaceAsync();
        const string key = "invalid-resource-01";
        string body = ProjectJson("Invalid resource receipt", "resource check");
        using HttpResponseMessage first = await SendMutationAsync(HttpMethod.Post, "/api/v1/projects/", body, key);
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        using JsonDocument source = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        Guid projectId = source.RootElement.GetProperty("id").GetGuid();
        string storedBody = await first.Content.ReadAsStringAsync();

        Guid otherOrganizationId = await CreateWorkspaceAsync();
        using HttpResponseMessage foreignCreated = await SendMutationAsync(HttpMethod.Post, "/api/v1/projects/",
            ProjectJson("Foreign receipt target", "other organization"), null);
        foreignCreated.StatusCode.ShouldBe(HttpStatusCode.Created);
        using JsonDocument foreign = JsonDocument.Parse(await foreignCreated.Content.ReadAsStringAsync());
        Guid foreignProjectId = foreign.RootElement.GetProperty("id").GetGuid();
        foreignProjectId.ShouldNotBe(projectId);
        await SelectWorkspaceAsync(organizationId);

        await SetReceiptResourceAsync(organizationId, key, foreignProjectId);
        using HttpResponseMessage foreignDenied = await SendMutationAsync(HttpMethod.Post, "/api/v1/projects/", body, key);
        foreignDenied.StatusCode.ShouldBeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
        foreignDenied.Headers.Location.ShouldBeNull();
        (await foreignDenied.Content.ReadAsStringAsync()).ShouldNotContain(storedBody);

        await SetReceiptResourceAsync(organizationId, key, Guid.CreateVersion7());
        using HttpResponseMessage missingDenied = await SendMutationAsync(HttpMethod.Post, "/api/v1/projects/", body, key);
        missingDenied.StatusCode.ShouldBeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
        missingDenied.Headers.Location.ShouldBeNull();
        (await missingDenied.Content.ReadAsStringAsync()).ShouldNotContain(storedBody);
        await SetReceiptResourceAsync(organizationId, key, projectId, "unknown-resource");
        using HttpResponseMessage wrongKey = await SendMutationAsync(HttpMethod.Post, "/api/v1/projects/", body, key);
        wrongKey.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        wrongKey.Headers.Location.ShouldBeNull();
        (await wrongKey.Content.ReadAsStringAsync()).ShouldNotContain(storedBody);
        (await ReadProjectSnapshotAsync(organizationId)).ShouldBe(new ProjectSnapshot(1, 1, 2, 1));
        (await ReadProjectSnapshotAsync(otherOrganizationId)).ShouldBe(new ProjectSnapshot(1, 1, 2, 0));
    }

    [TestMethod]
    public async Task ArchivedAndDeletionRequestedProjectStillReplaysWithoutProjectsReadPermission()
    {
        Guid organizationId = await CreateWorkspaceAsync();
        using HttpResponseMessage created = await SendMutationAsync(HttpMethod.Post, "/api/v1/projects/",
            ProjectJson("Archived receipt", "receipt survives lifecycle"), "lifecycle-create-01");
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        using JsonDocument body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Guid projectId = body.RootElement.GetProperty("id").GetGuid();

        using HttpResponseMessage archived = await SendMutationAsync(HttpMethod.Post, $"/api/v1/projects/{projectId:D}/archive", "{}", null);
        archived.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await RemovePermissionAsync(organizationId, "projects.read");
        using HttpResponseMessage deleted = await SendMutationAsync(HttpMethod.Delete, $"/api/v1/projects/{projectId:D}",
            "{\"reason\":\"Request deletion after archive\"}", null);
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using HttpResponseMessage replay = await SendMutationAsync(HttpMethod.Post, "/api/v1/projects/",
            ProjectJson("Archived receipt", "receipt survives lifecycle"), "lifecycle-create-01");

        replay.StatusCode.ShouldBe(HttpStatusCode.Created);
        replay.Headers.Location!.OriginalString.ShouldBe(created.Headers.Location!.OriginalString);
        byte[] originalBytes = await created.Content.ReadAsByteArrayAsync();
        (await replay.Content.ReadAsByteArrayAsync()).SequenceEqual(originalBytes).ShouldBeTrue();
        ProjectSnapshot snapshot = await ReadProjectSnapshotAsync(organizationId);
        snapshot.Projects.ShouldBe(1);
        snapshot.Audits.ShouldBe(3);
        snapshot.Outbox.ShouldBe(4);
        snapshot.Receipts.ShouldBe(1);
    }

    [TestMethod]
    public async Task ProjectsUpdateArchiveRestoreAndDeleteReplayTheirMutationResponses()
    {
        Guid organizationId = await CreateWorkspaceAsync();
        using HttpResponseMessage created = await SendMutationAsync(HttpMethod.Post, "/api/v1/projects/",
            ProjectJson("Lifecycle idempotency", "create once"), null);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        using JsonDocument createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Guid projectId = createdBody.RootElement.GetProperty("id").GetGuid();

        await AssertReplayAsync(HttpMethod.Put, $"/api/v1/projects/{projectId:D}",
            ProjectJson("Updated once", "update replay"), "update-key-01", HttpStatusCode.OK);
        await AssertReplayAsync(HttpMethod.Post, $"/api/v1/projects/{projectId:D}/archive", "{}", "archive-key-01", HttpStatusCode.NoContent);
        await AssertReplayAsync(HttpMethod.Post, $"/api/v1/projects/{projectId:D}/restore", "{}", "restore-key-01", HttpStatusCode.NoContent);
        await AssertReplayAsync(HttpMethod.Post, $"/api/v1/projects/{projectId:D}/archive", "{}", "archive-key-02", HttpStatusCode.NoContent);
        await AssertReplayAsync(HttpMethod.Delete, $"/api/v1/projects/{projectId:D}",
            "{\"reason\":\"Deletion requested\"}", "delete-key-01", HttpStatusCode.NoContent);

        ProjectSnapshot snapshot = await ReadProjectSnapshotAsync(organizationId);
        snapshot.Projects.ShouldBe(1);
        snapshot.Audits.ShouldBe(6);
        snapshot.Outbox.ShouldBe(7);
        snapshot.Receipts.ShouldBe(5);
    }

    [TestMethod]
    public async Task ValidationFailureLeavesTheKeyAvailableForACorrectedMutation()
    {
        Guid organization = await CreateWorkspaceAsync();
        using HttpResponseMessage invalid = await SendMutationAsync(HttpMethod.Post, "/api/v1/projects/",
            ProjectJson("", "Invalid domain input"), "validation-key");
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProjectSnapshotAsync(organization)).ShouldBe(new ProjectSnapshot(0, 0, 0, 0));
        using HttpResponseMessage corrected = await SendMutationAsync(HttpMethod.Post, "/api/v1/projects/",
            ProjectJson("Corrected", "Same key, corrected intent"), "validation-key");
        corrected.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await ReadProjectSnapshotAsync(organization)).ShouldBe(new ProjectSnapshot(1, 1, 2, 1));
    }

    [TestMethod]
    public async Task TheSameKeyHasIndependentReceiptsAcrossOrganizations()
    {
        Guid firstOrganization = await CreateWorkspaceAsync();
        string body = ProjectJson("Independent organizations", "same actor and intent");
        using HttpResponseMessage first = await SendMutationAsync(HttpMethod.Post, "/api/v1/projects/", body, "organization-key");
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        byte[] firstBytes = await first.Content.ReadAsByteArrayAsync();
        Guid secondOrganization = await CreateWorkspaceAsync();
        using HttpResponseMessage second = await SendMutationAsync(HttpMethod.Post, "/api/v1/projects/", body, "organization-key");
        second.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.Headers.Location.ShouldNotBe(first.Headers.Location);
        await SelectWorkspaceAsync(firstOrganization);
        using HttpResponseMessage replay = await SendMutationAsync(HttpMethod.Post, "/api/v1/projects/", body, "organization-key");
        (await replay.Content.ReadAsByteArrayAsync()).ShouldBe(firstBytes);
        (await ReadProjectSnapshotAsync(firstOrganization)).ShouldBe(new ProjectSnapshot(1, 1, 2, 1));
        (await ReadProjectSnapshotAsync(secondOrganization)).ShouldBe(new ProjectSnapshot(1, 1, 2, 1));
    }

    [TestMethod]
    public async Task TheSameKeyHasIndependentReceiptsAcrossActorsInTheSameOrganization()
    {
        Guid organization = await CreateWorkspaceAsync();
        string body = ProjectJson("Independent actors", "same organization and intent");
        using HttpResponseMessage first = await SendMutationAsync(HttpMethod.Post, "/api/v1/projects/", body, "actor-key");
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        byte[] ownerBytes = await first.Content.ReadAsByteArrayAsync();
        using HttpResponseMessage invitation = await PostAsync(browser, "/api/v1/invitations", new
        {
            email = $"idempotency-{Guid.NewGuid():N}@example.test", expiresInDays = 1
        });
        invitation.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement invited = await invitation.Content.ReadFromJsonAsync<JsonElement>();
        string token = new Uri(invited.GetProperty("invitationUrl").GetString()!).Segments.Last();
        using HttpClient member = host.CreateClient();
        using HttpResponseMessage activated = await PostAsync(member, "/api/v1/invitations/activate", new
        {
            token, firstName = "Receipt", lastName = "Member", password = SecurityHost.Password
        });
        activated.StatusCode.ShouldBe(HttpStatusCode.OK, await activated.Content.ReadAsStringAsync());
        using HttpResponseMessage independent = await SendMutationAsync(HttpMethod.Post, "/api/v1/projects/", body, "actor-key", client: member);
        independent.StatusCode.ShouldBe(HttpStatusCode.Created, await independent.Content.ReadAsStringAsync());
        independent.Headers.Location.ShouldNotBe(first.Headers.Location);
        using HttpResponseMessage replay = await SendMutationAsync(HttpMethod.Post, "/api/v1/projects/", body, "actor-key");
        (await replay.Content.ReadAsByteArrayAsync()).ShouldBe(ownerBytes);
        (await ReadProjectSnapshotAsync(organization)).ShouldBe(new ProjectSnapshot(2, 2, 4, 2));
    }

    [TestMethod]
    public async Task ArchivedMembershipCannotDiscloseItsPriorReceipt()
    {
        Guid organization = await CreateWorkspaceAsync();
        string body = ProjectJson("Membership receipt", "private response");
        using HttpResponseMessage first = await SendMutationAsync(HttpMethod.Post, "/api/v1/projects/", body, "membership-key");
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        string original = await first.Content.ReadAsStringAsync();
        await using NpgsqlConnection connection = new(host.OwnerConnection);
        await connection.OpenAsync();
        await using NpgsqlCommand archive = new("""
            UPDATE platform.memberships SET "ArchivedAt" = CURRENT_TIMESTAMP
            WHERE "OrganizationId" = @organization
            """, connection);
        archive.Parameters.AddWithValue("organization", organization);
        (await archive.ExecuteNonQueryAsync()).ShouldBe(1);
        using HttpResponseMessage denied = await SendMutationAsync(HttpMethod.Post, "/api/v1/projects/", body, "membership-key");
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        denied.Headers.Location.ShouldBeNull();
        (await denied.Content.ReadAsStringAsync()).ShouldNotContain(original);
        (await ReadProjectSnapshotAsync(organization)).ShouldBe(new ProjectSnapshot(1, 1, 2, 1));
    }

    private static async Task AssertReplayAsync(HttpMethod method, string path, string body, string key, HttpStatusCode expected)
    {
        using HttpResponseMessage first = await SendMutationAsync(method, path, body, key);
        first.StatusCode.ShouldBe(expected);
        byte[] firstBody = await first.Content.ReadAsByteArrayAsync();
        string? firstContentType = first.Content.Headers.ContentType?.ToString();
        string? firstLocation = first.Headers.Location?.OriginalString;
        using HttpResponseMessage replay = await SendMutationAsync(method, path, body, key);
        replay.StatusCode.ShouldBe(expected);
        replay.Content.Headers.ContentType?.ToString().ShouldBe(firstContentType);
        replay.Headers.Location?.OriginalString.ShouldBe(firstLocation);
        (await replay.Content.ReadAsByteArrayAsync()).SequenceEqual(firstBody).ShouldBeTrue();
    }

    private static async Task<Guid> CreateWorkspaceAsync() =>
        await WorkspaceSessionLifecycleTests.CreateOrganizationForCurrentUserAsync(
            browser, "idem-" + Guid.NewGuid().ToString("N")[..20]);

    private static string ProjectJson(string name, string description) => JsonSerializer.Serialize(new { name, description });

    private static async Task<HttpResponseMessage> SendMutationAsync(
        HttpMethod method,
        string path,
        string body,
        string? key = null,
        string? csrfToken = null,
        bool acquireCsrf = true,
        HttpClient? client = null)
    {
        client ??= browser;
        if (acquireCsrf && csrfToken is null) csrfToken = await GetCsrfTokenAsync(client);
        using HttpRequestMessage request = MutationRequest(method, path, body, csrfToken, key);
        return await client.SendAsync(request);
    }

    private static async Task SelectWorkspaceAsync(Guid organizationId)
    {
        using HttpResponseMessage response = await SendMutationAsync(HttpMethod.Post, "/api/v1/workspace/select",
            JsonSerializer.Serialize(new { organizationId, remember = false }), null);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private static async Task<string> GetCsrfTokenAsync(HttpClient? client = null)
    {
        using HttpResponseMessage response = await (client ?? browser).GetAsync("/api/v1/auth/antiforgery");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return payload.GetProperty("token").GetString()!;
    }

    private static HttpRequestMessage MutationRequest(HttpMethod method, string path, string body, string? csrfToken, string? key = null)
    {
        HttpRequestMessage request = new(method, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (csrfToken is not null) request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return request;
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
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ReadStoredKeyHashAsync(Guid organizationId)
    {
        await using NpgsqlConnection connection = new(host.OwnerConnection);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("SELECT \"KeyHash\" FROM app.mutation_receipts WHERE \"OrganizationId\" = @organization", connection);
        command.Parameters.AddWithValue("organization", organizationId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task RemovePermissionAsync(Guid organizationId, string permission)
    {
        await using NpgsqlConnection connection = new(host.OwnerConnection);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("""
            DELETE FROM platform.role_permissions AS grant_row
            USING platform.roles AS role
            WHERE grant_row."RoleId" = role."Id"
              AND role."OrganizationId" = @organization
              AND grant_row."Permission" = @permission
            """, connection);
        command.Parameters.AddWithValue("organization", organizationId);
        command.Parameters.AddWithValue("permission", permission);
        (await command.ExecuteNonQueryAsync()).ShouldBeGreaterThan(0);
    }

    private static async Task SetReceiptResourceAsync(Guid organizationId, string key, Guid resourceId, string resourceKey = "projects")
    {
        string keyHash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(key)));
        await using NpgsqlConnection connection = new(host.OwnerConnection);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("""
            UPDATE app.mutation_receipts
            SET "ResourceId" = @resource, "ResourceKey" = @resource_key
            WHERE "OrganizationId" = @organization AND "KeyHash" = @key_hash
            """, connection);
        command.Parameters.AddWithValue("resource", resourceId);
        command.Parameters.AddWithValue("resource_key", resourceKey);
        command.Parameters.AddWithValue("organization", organizationId);
        command.Parameters.AddWithValue("key_hash", keyHash);
        (await command.ExecuteNonQueryAsync()).ShouldBe(1);
    }

    private sealed record ProjectSnapshot(long Projects, long Audits, long Outbox, long Receipts);
}
