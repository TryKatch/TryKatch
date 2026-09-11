using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Application.Outbox;
using Trykatch.Identity;
using Trykatch.Infrastructure.Modules;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;
using Trykatch.Modules.Documents.Infrastructure;
using Trykatch.Modules.Projects.Infrastructure;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class OutboxRecoveryIntegrationTests
{
    private const string Email = "outbox-admin@trykatch.test";
    private const string Password = "Local-only!Outbox-Password-42";

    [TestMethod]
    public void ReplayPollingContractDeclaresPendingAndCompletedBodies()
    {
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../web/packages/api-client/openapi/Trykatch.Api.json"));
        using JsonDocument contract = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement responses = contract.RootElement.GetProperty("paths")
            .GetProperty("/api/v1/platform/outbox/replay-requests/{requestId}").GetProperty("get").GetProperty("responses");
        responses.GetProperty("200").GetProperty("content").GetProperty("application/json")
            .GetProperty("schema").GetProperty("$ref").GetString().ShouldEndWith("/OutboxReplayOutcome");
        responses.GetProperty("202").GetProperty("content").GetProperty("application/json")
            .GetProperty("schema").GetProperty("$ref").GetString().ShouldEndWith("/OutboxReplayPending");
    }

    [TestMethod]
    public async Task ProductionHostedWorkerExhaustsAndReplaysThroughAuthenticatedHttp()
    {
        await using RecoveryHost host = await RecoveryHost.StartAsync();
        Guid messageId = Guid.CreateVersion7();
        const string secretPayload = "{\"token\":\"must-never-be-returned\"}";
        await host.InsertAsync(messageId, secretPayload);

        await host.WaitForAsync(messageId, exhausted: true);

        using HttpClient anonymous = host.CreateClient();
        (await anonymous.GetAsync("/api/v1/platform/outbox/failures")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using HttpClient administrator = await host.SignInAsync();
        HttpResponseMessage failures = await administrator.GetAsync("/api/v1/platform/outbox/failures?page=1&pageSize=100");
        failures.StatusCode.ShouldBe(HttpStatusCode.OK);
        string safeBody = await failures.Content.ReadAsStringAsync();
        safeBody.ShouldContain(messageId.ToString());
        safeBody.ShouldNotContain("must-never-be-returned");

        Guid requestId = Guid.CreateVersion7();
        HttpResponseMessage withoutAntiforgery = await administrator.PostAsJsonAsync(
            $"/api/v1/platform/outbox/{messageId}/replay", new { requestId, expectedFailedGeneration = 0 });
        withoutAntiforgery.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        string token = await RecoveryHost.GetAntiforgeryAsync(administrator);
        (await PostReplayAsync(administrator, token, messageId, requestId, 0)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await PostReplayAsync(administrator, token, messageId, requestId, 0)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await PostReplayAsync(administrator, token, messageId, requestId, 1)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        Guid competingRequestId = Guid.CreateVersion7();
        (await PostReplayAsync(administrator, token, messageId, competingRequestId, 0)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await host.WaitForProcessedAsync(messageId, generation: 1);
        await host.WaitForOutcomeAsync(requestId);
        await host.WaitForOutcomeAsync(competingRequestId);
        JsonElement outcome = await administrator.GetFromJsonAsync<JsonElement>($"/api/v1/platform/outbox/replay-requests/{requestId}");
        outcome.GetProperty("outcome").GetString().ShouldBe("replayed");
        JsonElement competing = await administrator.GetFromJsonAsync<JsonElement>($"/api/v1/platform/outbox/replay-requests/{competingRequestId}");
        competing.GetProperty("outcome").GetString().ShouldBe("stale_generation");
        host.Transport.MessageIds.ShouldBe([messageId, messageId]);
    }

    private static async Task<HttpResponseMessage> PostReplayAsync(
        HttpClient client, string token, Guid messageId, Guid requestId, int generation)
    {
        using HttpRequestMessage replay = new(HttpMethod.Post, $"/api/v1/platform/outbox/{messageId}/replay")
        {
            Content = JsonContent.Create(new { requestId, expectedFailedGeneration = generation })
        };
        replay.Headers.Add("X-CSRF-TOKEN", token);
        return await client.SendAsync(replay);
    }

    [TestMethod]
    public async Task ACompletedRejectedRequestCannotLaterResetAnExhaustedGeneration()
    {
        ReplayReadBarrier barrier = new();
        await using RecoveryHost host = await RecoveryHost.StartAsync(pollInterval: "00:05:00", interceptor: barrier);
        await host.StopWorkerAsync();
        Guid messageId = Guid.CreateVersion7();
        Guid requestId = Guid.CreateVersion7();
        await host.InsertAsync(messageId, "{}");
        await host.InsertReplayRequestAsync(Guid.CreateVersion7(), requestId, messageId);
        await using AsyncServiceScope stale = host.Services.CreateAsyncScope();
        await using AsyncServiceScope winner = host.Services.CreateAsyncScope();
        barrier.Arm();
        Task<OutboxBatchResult> pending = stale.ServiceProvider.GetRequiredService<OutboxBatchProcessor>()
            .ProcessAsync(CancellationToken.None);
        try
        {
            await barrier.Reached.WaitAsync(TimeSpan.FromSeconds(5));
            OutboxBatchResult first = await winner.ServiceProvider.GetRequiredService<OutboxBatchProcessor>()
                .ProcessAsync(CancellationToken.None);
            first.Replayed.ShouldBe(0);
            first.Terminalized.ShouldBe(1);
        }
        finally
        {
            barrier.Release();
        }

        (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Replayed.ShouldBe(0);
        await host.WaitForAsync(messageId, exhausted: true);
        host.Transport.MessageIds.ShouldBe([messageId]);
        using HttpClient administrator = await host.SignInAsync();
        JsonElement outcome = await administrator.GetFromJsonAsync<JsonElement>($"/api/v1/platform/outbox/replay-requests/{requestId}");
        outcome.GetProperty("outcome").GetString().ShouldBe("not_exhausted");
    }

    [TestMethod]
    public async Task RequestIdCannotBeReusedByAnotherAuthorizedActor()
    {
        await using RecoveryHost host = await RecoveryHost.StartAsync();
        using HttpClient administrator = await host.SignInAsync();
        (HttpClient second, _) = await host.GrantAndSignInAsync(administrator,
            "outbox-second-admin@trykatch.test", "platform-administrator");
        using (second)
        {
            Guid messageId = Guid.CreateVersion7();
            Guid requestId = Guid.CreateVersion7();
            (await PostReplayAsync(administrator, await RecoveryHost.GetAntiforgeryAsync(administrator),
                messageId, requestId, 0)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
            (await PostReplayAsync(second, await RecoveryHost.GetAntiforgeryAsync(second),
                messageId, requestId, 0)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }
    }

    [TestMethod]
    public async Task RuntimeRolesCannotForgeOrMutateRecoveryHistory()
    {
        await using RecoveryHost host = await RecoveryHost.StartAsync();
        Guid actor = Guid.CreateVersion7();
        Guid request = Guid.CreateVersion7();
        Guid message = Guid.CreateVersion7();
        await host.InsertReplayRequestAsync(actor, request, message);

        await RecoveryHost.AssertDeniedAsync(host.Connections.Organization,
            "SELECT * FROM platform.outbox_replay_requests", PostgresErrorCodes.InsufficientPrivilege);
        await RecoveryHost.AssertDeniedAsync(host.Connections.Identity,
            "SELECT * FROM platform.outbox_recovery_events", PostgresErrorCodes.InsufficientPrivilege);
        await RecoveryHost.AssertDeniedAsync(host.Connections.Platform,
            "UPDATE platform.outbox_replay_requests SET \"MessageId\" = gen_random_uuid()", PostgresErrorCodes.InsufficientPrivilege);
        await RecoveryHost.AssertDeniedAsync(host.Connections.Platform,
            "DELETE FROM platform.outbox_replay_requests", PostgresErrorCodes.InsufficientPrivilege);
        await RecoveryHost.AssertDeniedAsync(host.Connections.Platform,
            "INSERT INTO platform.outbox_recovery_events (\"Id\",\"MessageId\",\"ReplayGeneration\",\"Outcome\",\"OccurredAt\") VALUES (gen_random_uuid(),gen_random_uuid(),0,'not_found',CURRENT_TIMESTAMP)", PostgresErrorCodes.InsufficientPrivilege);
        await RecoveryHost.AssertDeniedAsync(host.Connections.Outbox,
            "DELETE FROM platform.outbox_recovery_events", PostgresErrorCodes.InsufficientPrivilege);
        await RecoveryHost.AssertDeniedAsync(host.Connections.Platform,
            "SELECT * FROM platform.outbox_messages", PostgresErrorCodes.InsufficientPrivilege);
        await host.AssertActorForgeryDeniedAsync();
    }

    [TestMethod]
    public async Task OversizePayloadIsTerminalizedServerSideWithoutTransportMaterialization()
    {
        await using RecoveryHost host = await RecoveryHost.StartAsync();
        Guid messageId = Guid.CreateVersion7();
        await host.InsertAsync(messageId, JsonSerializer.Serialize(new { value = new string('x', 1_048_577) }));

        await host.WaitForAsync(messageId, exhausted: true);

        host.Transport.MessageIds.ShouldBeEmpty();
        (await host.ReadFailureCodeAsync(messageId)).ShouldBe("payload_too_large");
    }

    [TestMethod]
    [DataRow("save")]
    [DataRow("commit")]
    public async Task PublishSuccessThenTransientRollbackRedeliversSameIdAndConsumerDeduplicates(string seam)
    {
        await using RecoveryHost host = await RecoveryHost.StartAsync(transportFailures: 0);
        await host.InstallOneShotSerializationFailureAsync(seam);
        Guid messageId = Guid.CreateVersion7();
        await host.InsertAsync(messageId, "{}");

        await host.WaitForProcessedAsync(messageId, generation: 0);

        host.Transport.MessageIds.ShouldBe([messageId, messageId]);
        (await host.CountConsumerEffectsAsync()).ShouldBe(1);
        (await host.ReadFaultSeamCountAsync()).ShouldBeGreaterThanOrEqualTo(2);
        using HttpClient client = host.CreateClient();
        (await client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [TestMethod]
    [DataRow("open")]
    [DataRow("poll")]
    [DataRow("claim")]
    [DataRow("committed")]
    public async Task ReachedDatabaseFaultDisposesItsContextAndRecoversFromFreshState(string seam)
    {
        DatabaseFaultProbe probe = new(seam);
        await using RecoveryHost host = await RecoveryHost.StartAsync(transportFailures: 0,
            interceptors: [new ConnectionFault(probe), new QueryFault(probe), new CommittedFault(probe)]);
        Guid messageId = Guid.CreateVersion7();
        probe.Arm();
        await host.InsertAsync(messageId, "{}");

        await probe.Reached.WaitAsync(TimeSpan.FromSeconds(5));
        await host.WaitForProcessedAsync(messageId, 0);
        await RecoveryHost.WaitUntilAsync(() => Task.FromResult(probe.ContextIds.Count > 1));

        probe.Faults.ShouldBe(1);
        probe.FailedContext.ShouldNotBeNull();
        Should.Throw<ObjectDisposedException>(() => _ = probe.FailedContext.Database);
        host.Transport.MessageIds.ShouldBe([messageId]);
        (await host.CountConsumerEffectsAsync()).ShouldBe(1);
    }

    [TestMethod]
    public async Task ExhaustionAndReplaySurviveHostRestartWithDurableConsumerDeduplication()
    {
        await using RecoveryHost host = await RecoveryHost.StartAsync();
        Guid messageId = Guid.CreateVersion7();
        await host.InsertAsync(messageId, "{}");
        await host.WaitForAsync(messageId, true);
        await host.RestartAsync();
        using HttpClient administrator = await host.SignInAsync();
        Guid requestId = Guid.CreateVersion7();
        (await PostReplayAsync(administrator, await RecoveryHost.GetAntiforgeryAsync(administrator),
            messageId, requestId, 0)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await host.WaitForProcessedAsync(messageId, 1);
        await host.RestartAsync();
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(CancellationToken.None))
            .Published.ShouldBe(0);
        (await host.CountConsumerEffectsAsync()).ShouldBe(1);
        host.Transport.MessageIds.ShouldBe([messageId, messageId]);
    }

    [TestMethod]
    public async Task ShutdownDuringPublicationRollsBackWithoutRecordingATransportFailure()
    {
        await using RecoveryHost host = await RecoveryHost.StartAsync(transportFailures: 0);
        Guid messageId = Guid.CreateVersion7();
        host.Transport.PauseNext();
        await host.InsertAsync(messageId, "{}");
        await host.Transport.Reached.WaitAsync(TimeSpan.FromSeconds(5));

        await host.StopWorkerAsync().WaitAsync(TimeSpan.FromSeconds(5));

        (await host.CountPendingAsync()).ShouldBe(1);
        (await host.ReadAttemptsAsync(messageId)).ShouldBe(0);
        (await host.CountConsumerEffectsAsync()).ShouldBe(0);
        host.Transport.Release();
        await host.RestartAsync();
        await host.WaitForProcessedAsync(messageId, 0);
        (await host.ReadAttemptsAsync(messageId)).ShouldBe(0);
        (await host.CountConsumerEffectsAsync()).ShouldBe(1);
    }

    [TestMethod]
    public async Task PermanentSchemaFaultStopsDispatchAndMakesReadinessUnhealthy()
    {
        await using RecoveryHost host = await RecoveryHost.StartAsync(transportFailures: 0);
        await host.DropRecoveryColumnAsync();
        using HttpClient client = host.CreateClient();

        await RecoveryHost.WaitUntilAsync(async () =>
            (await client.GetAsync("/health/ready")).StatusCode == HttpStatusCode.ServiceUnavailable);

        host.Transport.MessageIds.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task TwoProductionScopesClaimDisjointStableBoundedBatches()
    {
        await using RecoveryHost host = await RecoveryHost.StartAsync(transportFailures: 0, pollInterval: "00:05:00");
        await host.StopWorkerAsync();
        await host.InsertManyAsync(120);
        await using AsyncServiceScope first = host.Services.CreateAsyncScope();
        await using AsyncServiceScope second = host.Services.CreateAsyncScope();

        host.Transport.PauseNext();
        Task<OutboxBatchResult> pending = first.ServiceProvider.GetRequiredService<OutboxBatchProcessor>()
            .ProcessAsync(CancellationToken.None);
        OutboxBatchResult completed;
        try
        {
            await host.Transport.Reached.WaitAsync(TimeSpan.FromSeconds(5));
            completed = await second.ServiceProvider.GetRequiredService<OutboxBatchProcessor>()
                .ProcessAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            completed.Published.ShouldBe(50);
            pending.IsCompleted.ShouldBeFalse();
        }
        finally
        {
            host.Transport.Release();
        }
        OutboxBatchResult[] batches = [await pending, completed];

        batches.Sum(x => x.Published).ShouldBe(100);
        host.Transport.MessageIds.Distinct().Count().ShouldBe(100);
        (await host.CountPendingAsync()).ShouldBe(20);
    }

    [TestMethod]
    public async Task CommittedProcessedStateIsNotRepublishedByFreshScope()
    {
        await using RecoveryHost host = await RecoveryHost.StartAsync(transportFailures: 0, pollInterval: "00:05:00");
        await host.StopWorkerAsync();
        Guid messageId = Guid.CreateVersion7();
        await host.InsertAsync(messageId, "{}");
        await using (AsyncServiceScope first = host.Services.CreateAsyncScope())
            (await first.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(CancellationToken.None)).Published.ShouldBe(1);
        int callsAfterCommit = host.Transport.MessageIds.Count;

        await using (AsyncServiceScope fresh = host.Services.CreateAsyncScope())
            (await fresh.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(CancellationToken.None)).Published.ShouldBe(0);

        host.Transport.MessageIds.Count.ShouldBe(callsAfterCommit);
        host.Transport.MessageIds.ShouldBe([messageId]);
    }

    [TestMethod]
    public async Task TransientBacklogLockFaultDoesNotTerminateHostAndRecovers()
    {
        await using RecoveryHost host = await RecoveryHost.StartAsync(transportFailures: 0);
        await host.StopWorkerAsync();
        Guid messageId = Guid.CreateVersion7();
        await host.InsertAsync(messageId, "{}");
        await using RecoveryHost.DatabaseBarrier barrier = await host.BlockOutboxAsync();
        await host.RestartAsync();
        using HttpClient client = host.CreateClient();
        await RecoveryHost.WaitUntilAsync(async () =>
            (await client.GetAsync("/health/ready")).StatusCode == HttpStatusCode.ServiceUnavailable);
        host.Transport.MessageIds.ShouldBeEmpty();

        await barrier.DisposeAsync();
        await host.WaitForProcessedAsync(messageId, 0);

        host.Transport.MessageIds.ShouldBe([messageId]);
        (await client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task ReplayHttpRateLimitIsBoundedPerAuthenticatedActor()
    {
        await using RecoveryHost host = await RecoveryHost.StartAsync();
        using HttpClient administrator = await host.SignInAsync();
        string token = await RecoveryHost.GetAntiforgeryAsync(administrator);
        HttpStatusCode[] statuses = new HttpStatusCode[11];
        for (int index = 0; index < statuses.Length; index++)
            statuses[index] = (await PostReplayAsync(administrator, token, Guid.CreateVersion7(), Guid.CreateVersion7(), 0)).StatusCode;

        statuses.Take(10).ShouldAllBe(status => status == HttpStatusCode.Accepted);
        statuses[^1].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [TestMethod]
    public async Task HttpRevalidatesReadReplayAndSuspendedPlatformAccess()
    {
        await using RecoveryHost host = await RecoveryHost.StartAsync();
        Guid messageId = Guid.CreateVersion7();
        await host.InsertAsync(messageId, "{}");
        await host.WaitForAsync(messageId, true);
        using HttpClient administrator = await host.SignInAsync();
        (HttpClient operatorClient, Guid operatorId) = await host.GrantAndSignInAsync(
            administrator, "outbox-operator@trykatch.test", "platform-operator");
        using (operatorClient)
        {
            (await operatorClient.GetAsync("/api/v1/platform/outbox/failures")).StatusCode.ShouldBe(HttpStatusCode.OK);
            string operatorToken = await RecoveryHost.GetAntiforgeryAsync(operatorClient);
            (await PostReplayAsync(operatorClient, operatorToken, messageId, Guid.CreateVersion7(), 0)).StatusCode
                .ShouldBe(HttpStatusCode.Forbidden);

            string administratorToken = await RecoveryHost.GetAntiforgeryAsync(administrator);
            using HttpRequestMessage suspend = new(HttpMethod.Post, $"/api/v1/platform-users/{operatorId}/suspend")
            { Content = JsonContent.Create(new { }) };
            suspend.Headers.Add("X-CSRF-TOKEN", administratorToken);
            (await administrator.SendAsync(suspend)).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await operatorClient.GetAsync("/api/v1/platform/outbox/failures")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
    }

    private sealed class RecoveryHost(
        PostgreSqlContainer postgres,
        WebApplicationFactory<Program> factory,
        string ownerConnection,
        RuntimeConnections connections,
        RecordingTransport transport,
        Func<WebApplicationFactory<Program>> createFactory) : IAsyncDisposable
    {
        public RuntimeConnections Connections => connections;
        public RecordingTransport Transport => transport;
        public IServiceProvider Services => factory.Services;

        public static async Task<RecoveryHost> StartAsync(int transportFailures = 1, string pollInterval = "00:00:00.100",
            DbCommandInterceptor? interceptor = null, IInterceptor[]? interceptors = null)
        {
            PostgreSqlContainer postgres = new PostgreSqlBuilder(
                "postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f").Build();
            await postgres.StartAsync();
            string owner = postgres.GetConnectionString();
            await PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(owner);
            (string organization, string platform, string identity, string outbox) =
                await PostgresRuntimeRoleFixture.CreateConnectionStringsAsync(owner);
            await MigrateAsync(owner);
            await PostgresRuntimeRoleFixture.GrantApplicationPrivilegesAsync(owner);

            await using (NpgsqlConnection consumer = new(owner))
            {
                await consumer.OpenAsync();
                await using NpgsqlCommand create = new("CREATE TABLE platform.test_consumer_effects (\"MessageId\" uuid PRIMARY KEY)", consumer);
                await create.ExecuteNonQueryAsync();
            }
            RecordingTransport transport = new(owner) { FailuresRemaining = transportFailures };
            TestClock clock = new();
            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:trykatchdb"] = organization,
                ["ConnectionStrings:trykatch-organization"] = organization,
                ["ConnectionStrings:trykatch-platform"] = platform,
                ["ConnectionStrings:trykatch-identity"] = identity,
                ["ConnectionStrings:trykatch-outbox"] = outbox,
                ["Bootstrap:PlatformAdminEmail"] = Email,
                ["Bootstrap:PlatformAdminPassword"] = Password,
                ["DevelopmentDemo:Enabled"] = "false",
                ["OutboxRecovery:MaximumAttempts"] = "1",
                ["OutboxRecovery:PollInterval"] = pollInterval,
                ["OutboxRecovery:RetryBaseDelay"] = "00:00:00.100",
                ["OutboxRecovery:RetryMaximumDelay"] = "00:00:00.100",
                ["OutboxRecovery:DatabaseLockTimeout"] = "00:00:00.100"
            };
            WebApplicationFactory<Program> CreateFactory() => new WebApplicationFactory<Program>().WithWebHostBuilder(webHost =>
            {
                webHost.UseEnvironment("Development");
                foreach ((string key, string? value) in settings) webHost.UseSetting(key, value);
                webHost.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
                webHost.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IOutboxTransport>();
                    services.AddSingleton<IOutboxTransport>(transport);
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(clock);
                    if (interceptor is not null)
                        services.AddDbContext<OutboxDbContext>(options => options.AddInterceptors(interceptor));
                    if (interceptors is not null)
                        services.AddDbContext<OutboxDbContext>(options => options.AddInterceptors(interceptors));
                });
            });
            WebApplicationFactory<Program> factory = CreateFactory();
            _ = factory.Services;
            return new(postgres, factory, owner, new(organization, platform, identity, outbox), transport, CreateFactory);
        }

        public async Task StopWorkerAsync() => await factory.Services.GetServices<IHostedService>()
            .OfType<OutboxProcessor>().Single().StopAsync(CancellationToken.None);

        public async Task RestartAsync()
        {
            await factory.DisposeAsync();
            factory = createFactory();
            _ = factory.Services;
        }

        public async Task<long> CountConsumerEffectsAsync()
        {
            await using NpgsqlConnection connection = new(ownerConnection);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("SELECT count(*) FROM platform.test_consumer_effects", connection);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        private static async Task MigrateAsync(string owner)
        {
            await using (IdentityDbContext identity = new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(owner).Options))
                await identity.Database.MigrateAsync();
            await using (PlatformDbContext platform = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(owner).Options))
                await platform.Database.MigrateAsync();
            ModuleCatalog catalog = new([new ProjectsModule(), new DocumentsModule()]);
            await using (ApplicationDbContext application = new(
                new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(owner).Options,
                [new ProjectsModelContributor(), new DocumentsModelContributor()], moduleCatalog: catalog))
                await application.Database.MigrateAsync();
            await using (NpgsqlConnection moduleConnection = new(owner))
            {
                await moduleConnection.OpenAsync();
                foreach (PendingModuleMigration migration in ModuleMigrationPlan.Build(catalog.Modules, []))
                {
                    await using NpgsqlCommand command = new(migration.Sql, moduleConnection);
                    await command.ExecuteNonQueryAsync();
                }
            }
            await InstalledSchemaCatalog.SynchronizeAsync(owner, catalog.Descriptors.SelectMany(module =>
                module.DataResources.Select(resource => new InstalledDataResource(module.Id, resource))));
        }

        public HttpClient CreateClient() => factory.CreateClient(new()
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        public async Task<HttpClient> SignInAsync(string email = Email)
        {
            HttpClient client = CreateClient();
            string token = await GetAntiforgeryAsync(client);
            using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/auth/login")
            {
                Content = JsonContent.Create(new { email, password = Password, rememberMe = false })
            };
            request.Headers.Add("X-CSRF-TOKEN", token);
            (await client.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.OK);
            return client;
        }

        public async Task<(HttpClient Client, Guid UserId)> GrantAndSignInAsync(HttpClient administrator, string email, string roleKey)
        {
            string adminToken = await GetAntiforgeryAsync(administrator);
            using HttpRequestMessage grant = new(HttpMethod.Post, "/api/v1/platform-users")
            { Content = JsonContent.Create(new { email, displayName = email, roleKey }) };
            grant.Headers.Add("X-CSRF-TOKEN", adminToken);
            HttpResponseMessage granted = await administrator.SendAsync(grant);
            granted.StatusCode.ShouldBe(HttpStatusCode.Created);
            JsonElement body = await granted.Content.ReadFromJsonAsync<JsonElement>();
            Guid userId = body.GetProperty("user").GetProperty("id").GetGuid();
            string activationToken = body.GetProperty("activationToken").GetString()!;
            using HttpClient activation = CreateClient();
            string activationCsrf = await GetAntiforgeryAsync(activation);
            using HttpRequestMessage activate = new(HttpMethod.Post, "/api/v1/access-activation")
            { Content = JsonContent.Create(new { userId, token = activationToken, password = Password }) };
            activate.Headers.Add("X-CSRF-TOKEN", activationCsrf);
            (await activation.SendAsync(activate)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
            return (await SignInAsync(email), userId);
        }

        public static async Task<string> GetAntiforgeryAsync(HttpClient client) =>
            (await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/antiforgery")).GetProperty("token").GetString()!;

        public async Task InsertAsync(Guid id, string payload)
        {
            await using NpgsqlConnection connection = new(ownerConnection);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("""
                INSERT INTO platform.outbox_messages ("Id", "Type", "Payload", "OccurredAt", "Attempts", "ReplayGeneration")
                VALUES (@id, 'recovery.test', @payload::jsonb, CURRENT_TIMESTAMP, 0, 0)
                """, connection);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("payload", payload);
            await command.ExecuteNonQueryAsync();
        }

        public async Task InsertManyAsync(int count)
        {
            for (int index = 0; index < count; index++)
                await InsertAsync(Guid.CreateVersion7(), "{}");
        }

        public async Task<long> CountPendingAsync()
        {
            await using NpgsqlConnection connection = new(ownerConnection);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("SELECT count(*) FROM platform.outbox_messages WHERE \"ProcessedAt\" IS NULL AND \"ExhaustedAt\" IS NULL", connection);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        public Task WaitForAsync(Guid id, bool exhausted) => WaitUntilAsync(async () =>
        {
            await using NpgsqlConnection connection = new(ownerConnection);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("SELECT (\"ExhaustedAt\" IS NOT NULL) FROM platform.outbox_messages WHERE \"Id\"=@id", connection);
            command.Parameters.AddWithValue("id", id);
            return await command.ExecuteScalarAsync() is bool value && value == exhausted;
        });

        public Task WaitForProcessedAsync(Guid id, int generation) => WaitUntilAsync(async () =>
        {
            await using NpgsqlConnection connection = new(ownerConnection);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("SELECT (\"ProcessedAt\" IS NOT NULL AND \"ReplayGeneration\"=@generation) FROM platform.outbox_messages WHERE \"Id\"=@id", connection);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("generation", generation);
            return await command.ExecuteScalarAsync() is true;
        });

        public Task WaitForOutcomeAsync(Guid requestId) => WaitUntilAsync(async () =>
        {
            await using NpgsqlConnection connection = new(ownerConnection);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("SELECT count(*) FROM platform.outbox_recovery_events WHERE \"RequestId\"=@request", connection);
            command.Parameters.AddWithValue("request", requestId);
            return (long)(await command.ExecuteScalarAsync())! == 1;
        });

        public async Task<string> ReadFailureCodeAsync(Guid id)
        {
            await using NpgsqlConnection connection = new(ownerConnection);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("SELECT \"LastErrorCode\" FROM platform.outbox_messages WHERE \"Id\"=@id", connection);
            command.Parameters.AddWithValue("id", id);
            return (string)(await command.ExecuteScalarAsync())!;
        }

        public async Task<int> ReadAttemptsAsync(Guid id)
        {
            await using NpgsqlConnection connection = new(ownerConnection);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("SELECT \"Attempts\" FROM platform.outbox_messages WHERE \"Id\"=@id", connection);
            command.Parameters.AddWithValue("id", id);
            return (int)(await command.ExecuteScalarAsync())!;
        }

        public async Task InstallOneShotSerializationFailureAsync(string seam)
        {
            await using NpgsqlConnection connection = new(ownerConnection);
            await connection.OpenAsync();
            string trigger = seam == "commit"
                ? "CREATE CONSTRAINT TRIGGER test_outbox_save_fault AFTER UPDATE OF \"ProcessedAt\" ON platform.outbox_messages DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION platform.test_outbox_save_fault();"
                : "CREATE TRIGGER test_outbox_save_fault BEFORE UPDATE OF \"ProcessedAt\" ON platform.outbox_messages FOR EACH ROW EXECUTE FUNCTION platform.test_outbox_save_fault();";
            await using NpgsqlCommand command = new($$"""
                CREATE SEQUENCE platform.test_outbox_save_fault;
                GRANT USAGE ON SEQUENCE platform.test_outbox_save_fault TO trykatch_outbox_worker;
                CREATE FUNCTION platform.test_outbox_save_fault() RETURNS trigger LANGUAGE plpgsql AS $body$
                BEGIN
                  IF nextval('platform.test_outbox_save_fault') = 1 THEN
                    RAISE EXCEPTION 'sentinel must not be logged' USING ERRCODE = '40001';
                  END IF;
                  RETURN NEW;
                END $body$;
                {{trigger}}
                """, connection);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<long> ReadFaultSeamCountAsync()
        {
            await using NpgsqlConnection connection = new(ownerConnection);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("SELECT last_value FROM platform.test_outbox_save_fault", connection);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        public async Task DropRecoveryColumnAsync()
        {
            await using NpgsqlConnection connection = new(ownerConnection);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("ALTER TABLE platform.outbox_messages DROP COLUMN \"ReplayGeneration\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<DatabaseBarrier> BlockOutboxAsync()
        {
            NpgsqlConnection connection = new(ownerConnection);
            await connection.OpenAsync();
            NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
            await using NpgsqlCommand command = new("LOCK TABLE platform.outbox_messages IN ACCESS EXCLUSIVE MODE", connection, transaction);
            await command.ExecuteNonQueryAsync();
            return new(connection, transaction);
        }

        public async Task InsertReplayRequestAsync(Guid actor, Guid request, Guid message)
        {
            await using NpgsqlConnection connection = new(connections.Platform);
            await connection.OpenAsync();
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
            await using NpgsqlCommand settings = new("SELECT set_config('app.actor_id', @actor, true)", connection, transaction);
            settings.Parameters.AddWithValue("actor", actor.ToString());
            await settings.ExecuteNonQueryAsync();
            await using NpgsqlCommand insert = new("INSERT INTO platform.outbox_replay_requests VALUES (@request,@message,0,@actor_id,CURRENT_TIMESTAMP)", connection, transaction);
            insert.Parameters.AddWithValue("request", request);
            insert.Parameters.AddWithValue("message", message);
            insert.Parameters.AddWithValue("actor_id", actor);
            await insert.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        public async Task AssertActorForgeryDeniedAsync()
        {
            await using NpgsqlConnection connection = new(connections.Platform);
            await connection.OpenAsync();
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
            await using NpgsqlCommand settings = new("SELECT set_config('app.actor_id', @actor, true)", connection, transaction);
            settings.Parameters.AddWithValue("actor", Guid.CreateVersion7().ToString());
            await settings.ExecuteNonQueryAsync();
            await using NpgsqlCommand insert = new("INSERT INTO platform.outbox_replay_requests VALUES (@request,@message,0,@forged,CURRENT_TIMESTAMP)", connection, transaction);
            insert.Parameters.AddWithValue("request", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("message", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("forged", Guid.CreateVersion7());
            (await Should.ThrowAsync<PostgresException>(() => insert.ExecuteNonQueryAsync())).SqlState
                .ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        public static async Task AssertDeniedAsync(string connectionString, string sql, string sqlState)
        {
            await using NpgsqlConnection connection = new(connectionString);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new(sql, connection);
            (await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState.ShouldBe(sqlState);
        }

        public static async Task WaitUntilAsync(Func<Task<bool>> probe)
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
            while (!await probe()) await Task.Delay(50, timeout.Token);
        }

        public async ValueTask DisposeAsync()
        {
            await factory.DisposeAsync();
            await postgres.DisposeAsync();
        }

        public sealed class DatabaseBarrier(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
        {
            private int disposed;
            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                await transaction.RollbackAsync();
                await transaction.DisposeAsync();
                await connection.DisposeAsync();
            }
        }
    }

    public sealed class RecordingTransport(string consumerConnection) : IOutboxTransport
    {
        private int failuresRemaining;
        private int pauseNext;
        private readonly TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int FailuresRemaining { get => failuresRemaining; set => failuresRemaining = value; }
        public List<Guid> MessageIds { get; } = [];
        public Task Reached => reached.Task;
        public void PauseNext() => Interlocked.Exchange(ref pauseNext, 1);
        public void Release() => released.TrySetResult();
        public async Task PublishAsync(OutboxEnvelope envelope, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (MessageIds) MessageIds.Add(envelope.MessageId);
            if (Interlocked.Exchange(ref pauseNext, 0) == 1)
            {
                reached.TrySetResult();
                await released.Task.WaitAsync(cancellationToken);
            }
            if (Interlocked.Decrement(ref failuresRemaining) >= 0)
                throw new HttpRequestException("password=never-log-this");
            await using NpgsqlConnection connection = new(consumerConnection);
            await connection.OpenAsync(cancellationToken);
            await using NpgsqlCommand command = new("INSERT INTO platform.test_consumer_effects (\"MessageId\") VALUES (@id) ON CONFLICT DO NOTHING", connection);
            command.Parameters.AddWithValue("id", envelope.MessageId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public sealed record RuntimeConnections(string Organization, string Platform, string Identity, string Outbox);

    private sealed class DatabaseFaultProbe(string seam)
    {
        private int armed;
        private int faults;
        private readonly TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Reached => reached.Task;
        public int Faults => Volatile.Read(ref faults);
        public DbContext? FailedContext { get; private set; }
        public System.Collections.Concurrent.ConcurrentDictionary<Guid, bool> ContextIds { get; } = new();
        public void Arm() { ContextIds.Clear(); Interlocked.Exchange(ref armed, 1); }
        public void Inspect(string currentSeam, DbContext? context)
        {
            if (context is not OutboxDbContext) return;
            ContextIds.TryAdd(context.ContextId.InstanceId, true);
            if (seam != currentSeam || Interlocked.CompareExchange(ref armed, 0, 1) != 1) return;
            FailedContext = context;
            ContextIds.Clear();
            ContextIds.TryAdd(context.ContextId.InstanceId, true);
            Interlocked.Increment(ref faults);
            reached.TrySetResult();
            throw new NpgsqlException("password=outage-sentinel", new IOException("transient fixture outage"));
        }
    }

    private sealed class ConnectionFault(DatabaseFaultProbe probe) : DbConnectionInterceptor
    {
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            probe.Inspect("open", eventData.Context);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class QueryFault(DatabaseFaultProbe probe) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("outbox_messages", StringComparison.Ordinal))
            {
                if (command.CommandText.Contains("SELECT * FROM", StringComparison.Ordinal))
                    probe.Inspect("claim", eventData.Context);
                else if (command.CommandText.Contains("count(", StringComparison.OrdinalIgnoreCase))
                    probe.Inspect("poll", eventData.Context);
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CommittedFault(DatabaseFaultProbe probe) : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context is OutboxDbContext context
                && context.ChangeTracker.Entries<OutboxMessage>().Any(entry => entry.Entity.ProcessedAt is not null))
                probe.Inspect("committed", context);
            return Task.CompletedTask;
        }
    }

    private sealed class ReplayReadBarrier : DbCommandInterceptor
    {
        private int armed;
        private readonly TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Reached => reached.Task;
        public void Arm() => Interlocked.Exchange(ref armed, 1);
        public void Release() => released.TrySetResult();
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("SELECT request.*", StringComparison.Ordinal)
                && Interlocked.CompareExchange(ref armed, 0, 1) == 1)
            {
                reached.TrySetResult();
                await released.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class TestClock : TimeProvider
    {
        private readonly DateTimeOffset now = new(2026, 9, 11, 21, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
    }
}
