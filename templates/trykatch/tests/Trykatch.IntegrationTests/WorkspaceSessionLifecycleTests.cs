using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Shouldly;
using static Trykatch.IntegrationTests.AccountSecurityStepUpTests;

namespace Trykatch.IntegrationTests;

[TestClass, TestCategory("Integration"), DoNotParallelize]
public sealed class WorkspaceSessionLifecycleTests
{
    [TestMethod]
    public async Task AcceptSelectReselectAndClearRotateDurableGeneration()
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient browser = host.CreateClient();
        var session = await AccountSessionLifecycleTests.SignInAsync(host, browser);
        SessionRow initial = await ReadSessionAsync(host, session.Hash);

        Guid organizationA = await CreateOrganizationForCurrentUserAsync(browser, "session-workspace-a");
        SessionRow acceptedA = await ReadSessionAsync(host, session.Hash);
        acceptedA.OrganizationId.ShouldBe(organizationA);
        acceptedA.WorkspaceGeneration.ShouldNotBe(initial.WorkspaceGeneration);

        Guid organizationB = await CreateOrganizationForCurrentUserAsync(browser, "session-workspace-b");
        SessionRow acceptedB = await ReadSessionAsync(host, session.Hash);
        acceptedB.OrganizationId.ShouldBe(organizationB);
        acceptedB.WorkspaceGeneration.ShouldNotBe(acceptedA.WorkspaceGeneration);

        using (HttpResponseMessage selectA = await SendWithAntiforgeryAsync(browser, HttpMethod.Post, "/api/v1/workspace/select",
                   new { organizationId = organizationA, remember = false }))
            selectA.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        SessionRow selectedA = await ReadSessionAsync(host, session.Hash);
        selectedA.OrganizationId.ShouldBe(organizationA);

        using (HttpResponseMessage selectB = await SendWithAntiforgeryAsync(browser, HttpMethod.Post, "/api/v1/workspace/select",
                   new { organizationId = organizationB, remember = false }))
            selectB.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        SessionRow selectedB = await ReadSessionAsync(host, session.Hash);
        selectedB.OrganizationId.ShouldBe(organizationB);

        using (HttpResponseMessage selectAAgain = await SendWithAntiforgeryAsync(browser, HttpMethod.Post, "/api/v1/workspace/select",
                   new { organizationId = organizationA, remember = false }))
            selectAAgain.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        SessionRow selectedAAgain = await ReadSessionAsync(host, session.Hash);
        selectedAAgain.OrganizationId.ShouldBe(organizationA);

        using (HttpResponseMessage reselectA = await SendWithAntiforgeryAsync(browser, HttpMethod.Post, "/api/v1/workspace/select",
                   new { organizationId = organizationA, remember = false }))
            reselectA.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        SessionRow reselectedA = await ReadSessionAsync(host, session.Hash);
        reselectedA.OrganizationId.ShouldBe(organizationA);

        using (HttpResponseMessage clear = await SendWithAntiforgeryAsync(browser, HttpMethod.Delete, "/api/v1/workspace/current"))
            clear.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        SessionRow cleared = await ReadSessionAsync(host, session.Hash);
        cleared.OrganizationId.ShouldBeNull();
        new[] { initial, acceptedA, acceptedB, selectedA, selectedB, selectedAAgain, reselectedA, cleared }
            .Select(row => row.WorkspaceGeneration).Distinct().Count().ShouldBe(8);
    }

    [TestMethod]
    public async Task FailedPasswordLoginClearsWorkspaceAndRotatesGenerationButKeepsSessionValid()
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient browser = host.CreateClient();
        var session = await AccountSessionLifecycleTests.SignInAsync(host, browser);
        Guid organizationId = await CreateOrganizationForCurrentUserAsync(browser, "failed-login-workspace");
        SessionRow selected = await ReadSessionAsync(host, session.Hash);
        selected.OrganizationId.ShouldBe(organizationId);

        using (HttpResponseMessage failedLogin = await PostAsync(browser, "/api/v1/auth/login", new
                   { email = SecurityHost.Email, password = "Incorrect!Password-42", rememberMe = false }))
            failedLogin.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        SessionRow cleared = await ReadSessionAsync(host, session.Hash);
        cleared.OrganizationId.ShouldBeNull();
        cleared.WorkspaceGeneration.ShouldNotBe(selected.WorkspaceGeneration);
        cleared.RevokedAt.ShouldBeNull();
        (await browser.GetAsync("/api/v1/auth/session")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task RemovedMembershipClearsDurableWorkspaceBindingAndRotatesGeneration()
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient administrator = host.CreateClient();
        await AccountSessionLifecycleTests.SignInAsync(host, administrator);
        Guid organizationId = await CreateOrganizationForCurrentUserAsync(administrator, "removed-member-workspace");

        const string memberEmail = "workspace-removed-member@trykatch.test";
        JsonElement invitation = await CreateInvitationAsync(administrator, memberEmail);
        using HttpClient member = host.CreateClient();
        await ActivateInvitationAsync(member, invitation, "Workspace", "Member");
        (string memberSessionHash, SessionRow beforeRemoval) = await ReadActiveSessionWithHashByEmailAsync(host, memberEmail);
        beforeRemoval.OrganizationId.ShouldBe(organizationId);
        Guid memberId = (await OrganizationMemberAsync(administrator, memberEmail)).GetProperty("id").GetGuid();

        using (HttpResponseMessage archive = await SendWithAntiforgeryAsync(administrator, HttpMethod.Post,
                   $"/api/v1/members/{memberId}/archive", new { }))
            archive.StatusCode.ShouldBe(HttpStatusCode.NoContent, await archive.Content.ReadAsStringAsync());
        using (HttpResponseMessage remove = await SendWithAntiforgeryAsync(administrator, HttpMethod.Delete,
                   $"/api/v1/members/{memberId}", new { reason = "Remove test account access" }))
            remove.StatusCode.ShouldBe(HttpStatusCode.NoContent, await remove.Content.ReadAsStringAsync());

        using (HttpResponseMessage denied = await member.GetAsync("/api/v1/workspace/current"))
            denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await denied.Content.ReadAsStringAsync());
        SessionRow cleared = await ReadSessionAsync(host, memberSessionHash);
        cleared.OrganizationId.ShouldBeNull();
        cleared.WorkspaceGeneration.ShouldNotBe(beforeRemoval.WorkspaceGeneration);
        cleared.RevokedAt.ShouldBeNull();
    }

    [TestMethod]
    public async Task InvitationActivationInExistingBrowserBindsNewIdentityAndReplacesOnlyThatBrowserSession()
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient browser = host.CreateClient();
        var original = await AccountSessionLifecycleTests.SignInAsync(host, browser);
        Guid organizationId = await CreateOrganizationForCurrentUserAsync(browser, "activation-workspace");
        using HttpClient otherDevice = host.CreateClient();
        var unrelated = await AccountSessionLifecycleTests.SignInAsync(host, otherDevice);
        JsonElement invitation = await CreateInvitationAsync(browser, "activated-in-same-browser@trykatch.test");

        using (HttpResponseMessage activation = await SendWithAntiforgeryAsync(browser, HttpMethod.Post,
                   "/api/v1/invitations/activate", new
                   {
                       token = InvitationToken(invitation),
                       firstName = "Activated", lastName = "Account", password = SecurityHost.Password
                   }))
            activation.StatusCode.ShouldBe(HttpStatusCode.OK);

        SessionRow oldBrowserRow = await ReadSessionAsync(host, original.Hash);
        oldBrowserRow.RevokedAt.ShouldNotBeNull();
        SessionRow unrelatedRow = await ReadSessionAsync(host, unrelated.Hash);
        unrelatedRow.RevokedAt.ShouldBeNull();
        (await otherDevice.GetAsync("/api/v1/auth/session")).StatusCode.ShouldBe(HttpStatusCode.OK);

        SessionRow activated = await ReadActiveSessionByEmailAsync(host, "activated-in-same-browser@trykatch.test");
        activated.OrganizationId.ShouldBe(organizationId);
        activated.WorkspaceGeneration.ShouldNotBe(Guid.Empty);
        (await browser.GetAsync("/api/v1/auth/session")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    internal static async Task<Guid> CreateOrganizationForCurrentUserAsync(HttpClient client, string slug)
    {
        using HttpResponseMessage create = await PostAsync(client, "/api/v1/tenants", new
        {
            name = slug.Replace('-', ' '), slug, administratorEmail = SecurityHost.Email, placement = "shared"
        });
        create.StatusCode.ShouldBe(HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        JsonElement response = (await create.Content.ReadFromJsonAsync<JsonElement>())!;
        Guid organizationId = response.GetProperty("organization").GetProperty("id").GetGuid();
        string token = response.GetProperty("invitationToken").GetString()!;

        using HttpResponseMessage accept = await PostAsync(client, "/api/v1/invitations/accept", new { token });
        accept.StatusCode.ShouldBe(HttpStatusCode.OK, await accept.Content.ReadAsStringAsync());
        return organizationId;
    }

    private static async Task<JsonElement> CreateInvitationAsync(HttpClient owner, string email)
    {
        using HttpResponseMessage response = await PostAsync(owner, "/api/v1/invitations", new { email, expiresInDays = 7 });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }

    private static async Task ActivateInvitationAsync(HttpClient invitee, JsonElement invitation, string firstName, string lastName)
    {
        using HttpResponseMessage response = await PostAsync(invitee, "/api/v1/invitations/activate", new
        {
            token = InvitationToken(invitation), firstName, lastName, password = SecurityHost.Password
        });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> OrganizationMemberAsync(HttpClient client, string email) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/v1/members")).EnumerateArray()
            .Single(member => member.GetProperty("email").GetString() == email);

    private static string InvitationToken(JsonElement invitation) =>
        new Uri(invitation.GetProperty("invitationUrl").GetString()!).Segments.Last();

    private static async Task<HttpResponseMessage> SendWithAntiforgeryAsync(HttpClient client, HttpMethod method, string path, object? body = null)
    {
        JsonElement csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/antiforgery");
        using HttpRequestMessage request = new(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        return await client.SendAsync(request);
    }

    private static async Task<SessionRow> ReadSessionAsync(SecurityHost host, string hash)
    {
        await using NpgsqlConnection connection = new(host.OwnerConnection);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("""
            SELECT "OrganizationId", "WorkspaceGeneration", "RevokedAt"
            FROM identity.account_sessions WHERE "SessionHash" = @hash
            """, connection);
        command.Parameters.AddWithValue("hash", hash);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        Guid? organizationId = reader.IsDBNull(0) ? null : reader.GetGuid(0);
        DateTimeOffset? revokedAt = reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2);
        return new(organizationId, reader.GetGuid(1), revokedAt);
    }

    private static async Task<SessionRow> ReadActiveSessionByEmailAsync(SecurityHost host, string email)
    {
        await using NpgsqlConnection connection = new(host.OwnerConnection);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("""
            SELECT session."OrganizationId", session."WorkspaceGeneration", session."RevokedAt"
            FROM identity.account_sessions AS session
            JOIN identity."AspNetUsers" AS account ON account."Id" = session."UserId"
            WHERE account."Email" = @email AND session."RevokedAt" IS NULL
            ORDER BY session."CreatedAt" DESC LIMIT 1
            """, connection);
        command.Parameters.AddWithValue("email", email);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        Guid? organizationId = reader.IsDBNull(0) ? null : reader.GetGuid(0);
        DateTimeOffset? revokedAt = reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2);
        return new(organizationId, reader.GetGuid(1), revokedAt);
    }

    private static async Task<(string Hash, SessionRow Row)> ReadActiveSessionWithHashByEmailAsync(SecurityHost host, string email)
    {
        await using NpgsqlConnection connection = new(host.OwnerConnection);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("""
            SELECT session."SessionHash", session."OrganizationId", session."WorkspaceGeneration", session."RevokedAt"
            FROM identity.account_sessions AS session
            JOIN identity."AspNetUsers" AS account ON account."Id" = session."UserId"
            WHERE account."Email" = @email AND session."RevokedAt" IS NULL
            ORDER BY session."CreatedAt" DESC LIMIT 1
            """, connection);
        command.Parameters.AddWithValue("email", email);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        string hash = reader.GetString(0);
        Guid? organizationId = reader.IsDBNull(1) ? null : reader.GetGuid(1);
        DateTimeOffset? revokedAt = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3);
        return (hash, new(organizationId, reader.GetGuid(2), revokedAt));
    }

    private sealed record SessionRow(Guid? OrganizationId, Guid WorkspaceGeneration, DateTimeOffset? RevokedAt);
}
