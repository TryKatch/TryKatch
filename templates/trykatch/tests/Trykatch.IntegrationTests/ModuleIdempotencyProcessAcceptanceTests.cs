using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Shouldly;

namespace Trykatch.IntegrationTests;

[TestClass, TestCategory("Integration"), DoNotParallelize]
public sealed class ModuleIdempotencyProcessAcceptanceTests
{
    [TestMethod]
    [DataRow("before-commit")]
    [DataRow("after-commit")]
    public async Task LostProductionProcessResponseRetriesOneCommittedMutation(string crashPhase)
    {
        await using RealtimeProcessAcceptanceFixture fixture = await RealtimeProcessAcceptanceFixture.StartAsync();
        RealtimeAcceptanceApiProcess first = await fixture.StartApiReplicaAsync("replica-a", mutationCrashPhase: crashPhase);
        using CookieApiClient owner = await fixture.CreateOrganizationOwnerAsync(first);
        JsonElement workspace = await owner.Client.GetFromJsonAsync<JsonElement>("/api/v1/workspace/current");
        Guid organization = workspace.GetProperty("organizationId").GetGuid();
        const string key = "production-crash-retry";
        const string body = "{\"name\":\"Process crash project\",\"description\":\"One committed intent\"}";
        string token = await AntiforgeryAsync(owner, new(first.Address));
        using HttpRequestMessage initialRequest = Request(new(first.Address), body, token, key);
        Task<HttpResponseMessage> lostResponse = owner.Client.SendAsync(initialRequest, HttpCompletionOption.ResponseHeadersRead);
        ReceiptSnapshot? committedBeforeKill = null;
        try
        {
            await first.WaitForMutationCheckpointAsync(crashPhase, TimeSpan.FromSeconds(20));
            lostResponse.IsCompleted.ShouldBeFalse("the client must not receive even the success headers before the crash");
            bool committed = crashPhase == "after-commit";
            (await CountsAsync(fixture.OwnerConnection, organization)).ShouldBe(
                committed ? new MutationCounts(1, 1, 2, 1) : new MutationCounts(0, 0, 0, 0));
            committedBeforeKill = await ReceiptAsync(fixture.OwnerConnection, organization, key);
            if (committed) committedBeforeKill.ShouldNotBeNull("the network checkpoint must follow a confirmed database commit");
            else committedBeforeKill.ShouldBeNull("business effects and receipt must remain uncommitted together");
        }
        finally
        {
            await first.KillAsync();
            await Should.ThrowAsync<HttpRequestException>(async () =>
            {
                using HttpResponseMessage unexpected = await lostResponse.WaitAsync(TimeSpan.FromSeconds(10));
            });
        }

        RealtimeAcceptanceApiProcess second = await fixture.StartApiReplicaAsync("replica-b");
        second.ProcessId.ShouldNotBe(first.ProcessId);
        await RealtimeProcessAcceptanceFixture.AssertCookieWorksOnOtherHostAsync(owner, second);
        string retryToken = await AntiforgeryAsync(owner, new(second.Address));
        using HttpRequestMessage retryRequest = Request(new(second.Address), body, retryToken, key);
        using HttpResponseMessage retry = await owner.Client.SendAsync(retryRequest);
        retry.StatusCode.ShouldBe(HttpStatusCode.Created, await retry.Content.ReadAsStringAsync());
        byte[] retryBody = await retry.Content.ReadAsByteArrayAsync();
        ReceiptSnapshot receipt = (await ReceiptAsync(fixture.OwnerConnection, organization, key)).ShouldNotBeNull();
        AssertReceipt(retry, retryBody, committedBeforeKill ?? receipt);
        (await CountsAsync(fixture.OwnerConnection, organization)).ShouldBe(new MutationCounts(1, 1, 2, 1));

        using HttpRequestMessage replayRequest = Request(new(second.Address), body, retryToken, key);
        using HttpResponseMessage replay = await owner.Client.SendAsync(replayRequest);
        AssertReceipt(replay, await replay.Content.ReadAsByteArrayAsync(), receipt);
        (await CountsAsync(fixture.OwnerConnection, organization)).ShouldBe(new MutationCounts(1, 1, 2, 1));
    }

    private static void AssertReceipt(HttpResponseMessage response, byte[] body, ReceiptSnapshot receipt)
    {
        ((int)response.StatusCode).ShouldBe(receipt.StatusCode);
        response.Content.Headers.ContentType?.ToString().ShouldBe(receipt.ContentType);
        response.Headers.Location?.OriginalString.ShouldBe(receipt.Location);
        body.SequenceEqual(receipt.Body).ShouldBeTrue();
        JsonElement project = JsonSerializer.Deserialize<JsonElement>(body);
        receipt.Location.ShouldBe($"/api/v1/projects/{project.GetProperty("id").GetGuid()}");
        project.GetProperty("name").GetString().ShouldBe("Process crash project");
    }

    private static async Task<string> AntiforgeryAsync(CookieApiClient owner, Uri address)
    {
        using HttpResponseMessage response = await owner.Client.GetAsync(new Uri(address, "/api/v1/auth/antiforgery"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        JsonElement csrf = await response.Content.ReadFromJsonAsync<JsonElement>();
        return csrf.GetProperty("token").GetString()!;
    }

    private static HttpRequestMessage Request(Uri address, string body, string token, string key)
    {
        HttpRequestMessage request = new(HttpMethod.Post, new Uri(address, "/api/v1/projects/"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-CSRF-TOKEN", token);
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private static async Task<MutationCounts> CountsAsync(string owner, Guid organization)
    {
        await using NpgsqlConnection connection = new(owner);
        await connection.OpenAsync();
        long projects = await CountAsync(connection, "SELECT count(*) FROM app.projects WHERE \"OrganizationId\" = @organization", organization);
        long audits = await CountAsync(connection, "SELECT count(*) FROM platform.audit_entries WHERE \"OrganizationId\" = @organization AND \"Action\" LIKE 'project.%'", organization);
        long outbox = await CountAsync(connection, """
            SELECT count(*) FROM platform.outbox_messages
            WHERE ("Type" = 'TrykatchApp.Application.Projects.ProjectChanged' AND "Payload"->>'organizationId' = @organization_text)
               OR ("Type" = 'projects.created' AND "ContextJson"->>'organizationId' = @organization_text)
            """, organization);
        long receipts = await CountAsync(connection, "SELECT count(*) FROM app.mutation_receipts WHERE \"OrganizationId\" = @organization", organization);
        return new(projects, audits, outbox, receipts);
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, string sql, Guid organization)
    {
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("organization", organization);
        command.Parameters.AddWithValue("organization_text", organization.ToString());
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<ReceiptSnapshot?> ReceiptAsync(string owner, Guid organization, string key)
    {
        await using NpgsqlConnection connection = new(owner);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("""
            SELECT "StatusCode", "ContentType", "Location", "Body" FROM app.mutation_receipts
            WHERE "OrganizationId" = @organization AND "ModuleId" = 'projects'
              AND "OperationId" = 'Projects_Create' AND "KeyHash" = @key
            """, connection);
        command.Parameters.AddWithValue("organization", organization);
        command.Parameters.AddWithValue("key", Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(key))));
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        ReceiptSnapshot receipt = new(reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetFieldValue<byte[]>(3));
        (await reader.ReadAsync()).ShouldBeFalse();
        return receipt;
    }

    private sealed record MutationCounts(long Projects, long Audits, long Outbox, long Receipts);
    private sealed record ReceiptSnapshot(int StatusCode, string? ContentType, string? Location, byte[] Body);
}
