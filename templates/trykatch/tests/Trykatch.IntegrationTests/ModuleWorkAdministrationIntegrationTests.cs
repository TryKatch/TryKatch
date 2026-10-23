using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Trykatch.Application.Jobs;
using Shouldly;

namespace Trykatch.IntegrationTests;

public sealed partial class OutboxRecoveryIntegrationTests
{
    [TestMethod]
    public async Task ModuleWorkInspectionRequiresExplicitAccessAndReturnsOnlySafeFields()
    {
        await using RecoveryHost host = await RecoveryHost.StartAsync();
        Guid work = await host.InsertModuleWorkAsync("queued");
        using HttpClient anonymous = host.CreateClient();
        (await anonymous.GetAsync("/api/v1/platform/module-work")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using HttpClient administrator = await host.SignInAsync();
        HttpResponseMessage result = await administrator.GetAsync("/api/v1/platform/module-work?page=-1&pageSize=1000");
        result.StatusCode.ShouldBe(HttpStatusCode.OK);
        string json = await result.Content.ReadAsStringAsync();
        json.ShouldContain(work.ToString());
        json.ShouldContain("unclassified");
        json.ShouldNotContain("private-work-marker");
        json.ShouldNotContain("executionContext");
        json.ShouldNotContain("payload");
        JsonElement page = JsonSerializer.Deserialize<JsonElement>(json);
        page.GetProperty("page").GetInt32().ShouldBe(1);
        page.GetProperty("pageSize").GetInt32().ShouldBe(100);
        (await administrator.GetAsync("/api/v1/platform/module-work?state=unknown")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (HttpClient reader, _) = await host.GrantAndSignInAsync(administrator, "work-operator@trykatch.test", "platform-operator");
        using (reader)
            (await reader.GetAsync("/api/v1/platform/module-work")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [TestMethod]
    public async Task ModuleWorkControlsAreAtomicIdempotentAndRequireCurrentGrantAndAntiforgery()
    {
        await using RecoveryHost host = await RecoveryHost.StartAsync();
        Guid work = await host.InsertModuleWorkAsync("queued");
        using HttpClient administrator = await host.SignInAsync();
        object body = new { requestId = Guid.NewGuid(), expectedGeneration = 0 };
        (await administrator.PostAsJsonAsync($"/api/v1/platform/module-work/{work}/pause", body)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        string token = await RecoveryHost.GetAntiforgeryAsync(administrator);
        async Task<HttpResponseMessage> ControlAsync(string action, object request)
        {
            using HttpRequestMessage message = new(HttpMethod.Post, $"/api/v1/platform/module-work/{work}/{action}")
            { Content = JsonContent.Create(request) };
            message.Headers.Add("X-CSRF-TOKEN", token);
            return await administrator.SendAsync(message);
        }
        (await ControlAsync("pause", body)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ControlAsync("pause", body)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await host.ReadWorkControlCountAsync(work)).ShouldBe(1);
        (await ControlAsync("resume", new { requestId = Guid.NewGuid(), expectedGeneration = 0 })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ControlAsync("resume", new { requestId = Guid.NewGuid(), expectedGeneration = 1 })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await host.ReadWorkControlCountAsync(work)).ShouldBe(2);

        (HttpClient formerAdministrator, Guid actor) = await host.GrantAndSignInAsync(administrator,
            "work-admin@trykatch.test", "platform-administrator");
        using (formerAdministrator)
        {
            string actorToken = await RecoveryHost.GetAntiforgeryAsync(formerAdministrator);
            Guid requestId = Guid.NewGuid();
            async Task<HttpResponseMessage> PauseAsync()
            {
                using HttpRequestMessage pause = new(HttpMethod.Post, $"/api/v1/platform/module-work/{work}/pause")
                { Content = JsonContent.Create(new { requestId, expectedGeneration = 2 }) };
                pause.Headers.Add("X-CSRF-TOKEN", actorToken);
                return await formerAdministrator.SendAsync(pause);
            }
            (await PauseAsync()).StatusCode.ShouldBe(HttpStatusCode.OK);
            using HttpRequestMessage suspend = new(HttpMethod.Post, $"/api/v1/platform-users/{actor}/suspend") { Content = JsonContent.Create(new { }) };
            suspend.Headers.Add("X-CSRF-TOKEN", token);
            (await administrator.SendAsync(suspend)).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await PauseAsync()).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await host.ReadWorkControlCountAsync(work)).ShouldBe(3);
        }
    }

    [TestMethod]
    public async Task ModuleWorkRetryPreservesIdentityAndRejectsCompletedOrLeasedRows()
    {
        await using RecoveryHost host = await RecoveryHost.StartAsync();
        using HttpClient administrator = await host.SignInAsync();
        string token = await RecoveryHost.GetAntiforgeryAsync(administrator);
        foreach ((string state, HttpStatusCode expected) in new[]
                 { ("terminal", HttpStatusCode.OK), ("completed", HttpStatusCode.Conflict), ("leased", HttpStatusCode.Conflict) })
        {
            Guid work = await host.InsertModuleWorkAsync(state);
            using HttpRequestMessage message = new(HttpMethod.Post, $"/api/v1/platform/module-work/{work}/retry")
            { Content = JsonContent.Create(new { requestId = Guid.NewGuid(), expectedGeneration = 0 }) };
            message.Headers.Add("X-CSRF-TOKEN", token);
            HttpResponseMessage response = await administrator.SendAsync(message);
            response.StatusCode.ShouldBe(expected);
            if (expected == HttpStatusCode.OK)
                (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("workId").GetGuid().ShouldBe(work);
        }
    }

    [TestMethod]
    public async Task ConcurrentControlsShareOneRequestIdentityAcrossDifferentWorkRows()
    {
        await using RecoveryHost host = await RecoveryHost.StartAsync();
        using HttpClient administrator = await host.SignInAsync();
        string token = await RecoveryHost.GetAntiforgeryAsync(administrator);
        Guid requestId = Guid.NewGuid();
        Guid[] rows = [await host.InsertModuleWorkAsync("queued"), await host.InsertModuleWorkAsync("queued"),
            await host.InsertModuleWorkAsync("queued")];
        async Task<HttpStatusCode> PauseAsync(Guid workId)
        {
            using HttpRequestMessage message = new(HttpMethod.Post, $"/api/v1/platform/module-work/{workId}/pause")
            { Content = JsonContent.Create(new { requestId, expectedGeneration = 0 }) };
            message.Headers.Add("X-CSRF-TOKEN", token);
            return (await administrator.SendAsync(message)).StatusCode;
        }
        HttpStatusCode[] responses = await Task.WhenAll(rows.Select(PauseAsync));
        responses.Count(status => status == HttpStatusCode.OK).ShouldBe(1);
        responses.Count(status => status == HttpStatusCode.Conflict).ShouldBe(2);
        long[] counts = await Task.WhenAll(rows.Select(host.ReadWorkControlCountAsync));
        counts.Sum().ShouldBe(1);
    }

    [TestMethod]
    public async Task ModuleWorkControlRollsBackWhenItsAuditInsertFails()
    {
        await using RecoveryHost host = await RecoveryHost.StartAsync();
        using HttpClient administrator = await host.SignInAsync();
        (HttpClient delegated, Guid actor) = await host.GrantAndSignInAsync(administrator,
            "atomic-work-admin@trykatch.test", "platform-administrator");
        using (delegated)
        {
            Guid work = await host.InsertModuleWorkAsync("queued");
            await host.RejectWorkControlAuditAsync(work);
            await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
            IModuleWorkAdministration controls = scope.ServiceProvider.GetRequiredService<IModuleWorkAdministration>();
            DbUpdateException exception = await Should.ThrowAsync<DbUpdateException>(() => controls.ChangeAsync(
                actor, work, "pause", new ModuleWorkActionRequest(Guid.NewGuid(), 0)));
            ((PostgresException)exception.InnerException!).SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            (await host.ReadWorkControlCountAsync(work)).ShouldBe(0);
            (await host.IsWorkOperatorPausedAsync(work)).ShouldBeFalse();
        }
    }

    private sealed partial class RecoveryHost
    {
        public async Task RejectWorkControlAuditAsync(Guid workId)
        {
            await using NpgsqlConnection connection = new(ownerConnection);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new(
                $"ALTER TABLE infrastructure.module_work_controls ADD CONSTRAINT e4_reject_control CHECK (\"WorkId\" <> '{workId:D}'::uuid) NOT VALID", connection);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<bool> IsWorkOperatorPausedAsync(Guid workId)
        {
            await using NpgsqlConnection connection = new(ownerConnection);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("SELECT \"OperatorPaused\" FROM infrastructure.module_work_items WHERE \"Id\"=@id", connection);
            command.Parameters.AddWithValue("id", workId);
            return (bool)(await command.ExecuteScalarAsync())!;
        }

        public async Task<Guid> InsertModuleWorkAsync(string state)
        {
            Guid id = Guid.NewGuid();
            await using NpgsqlConnection connection = new(ownerConnection);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("""
                INSERT INTO infrastructure.module_work_items
                ("Id","OrganizationId","ActorId","RequestId","ExecutionIdentityKey","JobKey","JobVersion","OperationKey","OperationVersion",
                 "ContextVersion","ExecutionContextJson","Payload","PayloadHash","IntentHash","SubmissionMode","State","Attempts","CreatedAtUtc",
                 "OperatorPaused","RecoveryGeneration","LastFailureCode")
                VALUES (@id,@organization,@actor,@request,'probe/user','probe.command',1,'probe.command',1,1,'{}',
                    '{"private":"private-work-marker"}',repeat('a',64),repeat('b',64),'enqueue',@state,0,CURRENT_TIMESTAMP,false,0,'private-work-marker')
                """, connection);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("organization", Guid.NewGuid());
            command.Parameters.AddWithValue("actor", Guid.NewGuid());
            command.Parameters.AddWithValue("request", Guid.NewGuid());
            command.Parameters.AddWithValue("state", state);
            await command.ExecuteNonQueryAsync();
            return id;
        }

        public async Task<long> ReadWorkControlCountAsync(Guid workId)
        {
            await using NpgsqlConnection connection = new(ownerConnection);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("SELECT count(*) FROM infrastructure.module_work_controls WHERE \"WorkId\"=@id", connection);
            command.Parameters.AddWithValue("id", workId);
            return (long)(await command.ExecuteScalarAsync())!;
        }
    }
}
