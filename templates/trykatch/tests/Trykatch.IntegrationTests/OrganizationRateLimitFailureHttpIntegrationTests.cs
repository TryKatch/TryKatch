using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Shouldly;

namespace Trykatch.IntegrationTests;

public sealed partial class AccessManagementBoundaryTests
{
    [TestMethod]
    public async Task ReplayAttemptsConsumeAllowanceWithoutRepeatingBusinessEffects()
    {
        await using AccessHost setup = await AccessHost.StartAsync();
        using HttpClient administrator = await setup.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient setupOwner = await setup.CreateOrganizationOwnerAsync(administrator);
        Guid organizationId = (await setupOwner.GetFromJsonAsync<JsonElement>("/api/v1/access"))
            .GetProperty("organizationId").GetGuid();

        await using AccessHost replica = await setup.CreateReplicaAsync(RequestLimitSettings(permitLimit: 2, windowSeconds: 60));
        using HttpClient owner = await replica.SignInAsync("owner@trykatch.test");
        await AccessHost.SuccessAsync(owner, HttpMethod.Post, "/api/v1/workspace/select", new { organizationId, remember = false });

        const string key = "organization-limit-replay-key";
        using HttpResponseMessage created = await SendProjectsFeatureMutationAsync(owner, key, "Limited project", "Created once.");
        using HttpResponseMessage replay = await SendProjectsFeatureMutationAsync(owner, key, "Limited project", "Created once.");
        using HttpResponseMessage exhausted = await SendProjectsFeatureMutationAsync(owner, key, "Limited project", "Created once.");

        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        replay.StatusCode.ShouldBe(HttpStatusCode.Created, await replay.Content.ReadAsStringAsync());
        exhausted.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await ReadAttemptsAsync(setup.OwnerConnection, organizationId)).ShouldBe(2);
        (await ReadProjectEffectsAsync(setup.OwnerConnection, organizationId))
            .ShouldBe((Projects: 1L, Audits: 1L, Receipts: 1L));
    }

    [TestMethod]
    public async Task HandlerValidationFailureConsumesAllowanceBeforeLaterReadIsRejected()
    {
        await using AccessHost setup = await AccessHost.StartAsync();
        using HttpClient administrator = await setup.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient setupOwner = await setup.CreateOrganizationOwnerAsync(administrator);
        Guid organizationId = (await setupOwner.GetFromJsonAsync<JsonElement>("/api/v1/access"))
            .GetProperty("organizationId").GetGuid();

        await using AccessHost replica = await setup.CreateReplicaAsync(RequestLimitSettings(permitLimit: 1, windowSeconds: 60));
        using HttpClient owner = await replica.SignInAsync("owner@trykatch.test");
        await AccessHost.SuccessAsync(owner, HttpMethod.Post, "/api/v1/workspace/select", new { organizationId, remember = false });

        using HttpResponseMessage invalid = await SendProjectsFeatureMutationAsync(owner, "invalid-project-key", "", "Invalid name.");
        using HttpResponseMessage exhausted = await owner.GetAsync("/api/v1/access");

        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await invalid.Content.ReadAsStringAsync());
        exhausted.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await ReadAttemptsAsync(setup.OwnerConnection, organizationId)).ShouldBe(1);
        (await ReadProjectEffectsAsync(setup.OwnerConnection, organizationId))
            .ShouldBe((Projects: 0L, Audits: 0L, Receipts: 0L));
    }

    [TestMethod]
    public async Task CounterRowLockReturnsBoundedSanitizedUnavailableAndDoesNotConsumeAttempt()
    {
        await using AccessHost setup = await AccessHost.StartAsync();
        using HttpClient administrator = await setup.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient setupOwner = await setup.CreateOrganizationOwnerAsync(administrator);
        Guid organizationId = (await setupOwner.GetFromJsonAsync<JsonElement>("/api/v1/access"))
            .GetProperty("organizationId").GetGuid();

        await using AccessHost replica = await setup.CreateReplicaAsync(RequestLimitSettings(permitLimit: 2, windowSeconds: 60));
        using HttpClient owner = await replica.SignInAsync("owner@trykatch.test");
        await AccessHost.SuccessAsync(owner, HttpMethod.Post, "/api/v1/workspace/select", new { organizationId, remember = false });
        using (HttpResponseMessage first = await owner.GetAsync("/api/v1/access"))
            first.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using NpgsqlConnection lockConnection = new(setup.OwnerConnection);
        await lockConnection.OpenAsync();
        await using NpgsqlTransaction lockTransaction = await lockConnection.BeginTransactionAsync();
        await using (NpgsqlCommand lockRow = new("""
            SELECT 1 FROM app.organization_request_limits
            WHERE "OrganizationId" = @organization AND "Policy" = 'organization-api'
            FOR UPDATE
            """, lockConnection, lockTransaction))
        {
            lockRow.Parameters.AddWithValue("organization", organizationId);
            (await lockRow.ExecuteScalarAsync()).ShouldNotBeNull();
        }

        using HttpResponseMessage unavailable = await owner.GetAsync("/api/v1/access").WaitAsync(TimeSpan.FromSeconds(5));
        unavailable.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        string problem = await unavailable.Content.ReadAsStringAsync();
        using (JsonDocument document = JsonDocument.Parse(problem))
            document.RootElement.GetProperty("title").GetString().ShouldBe("organization_request_limit_unavailable");
        problem.ShouldNotContain("Npgsql");
        problem.ShouldNotContain("Host=");
        (await ReadAttemptsAsync(setup.OwnerConnection, organizationId)).ShouldBe(1);

        await lockTransaction.RollbackAsync();
        using HttpResponseMessage recovered = await owner.GetAsync("/api/v1/access");
        recovered.StatusCode.ShouldBe(HttpStatusCode.OK, await recovered.Content.ReadAsStringAsync());
        (await ReadAttemptsAsync(setup.OwnerConnection, organizationId)).ShouldBe(2);
    }

    private static async Task<HttpResponseMessage> SendProjectsFeatureMutationAsync(
        HttpClient client, string idempotencyKey, string name, string description)
    {
        JsonElement antiforgery = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/antiforgery");
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/projects/")
        {
            Content = JsonContent.Create(new { name, description })
        };
        request.Headers.Add("X-CSRF-TOKEN", antiforgery.GetProperty("token").GetString());
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private static async Task<(long Projects, long Audits, long Receipts)> ReadProjectEffectsAsync(
        string connectionString, Guid organizationId)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("""
            SELECT
                (SELECT count(*) FROM app.projects WHERE "OrganizationId" = @organization),
                (SELECT count(*) FROM platform.audit_entries WHERE "OrganizationId" = @organization AND "Action" LIKE 'project.%'),
                (SELECT count(*) FROM app.mutation_receipts WHERE "OrganizationId" = @organization)
            """, connection);
        command.Parameters.AddWithValue("organization", organizationId);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }
}
