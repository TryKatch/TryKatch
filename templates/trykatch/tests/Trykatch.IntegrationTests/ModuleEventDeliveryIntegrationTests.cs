using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Application;
using Trykatch.Application.Execution;
using Trykatch.Application.Outbox;
using Trykatch.Domain.Organizations;
using Trykatch.Identity;
using Trykatch.Infrastructure;
using Trykatch.Infrastructure.Eventing;
using Trykatch.Infrastructure.Execution;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;
using Trykatch.Modules.Documents.Infrastructure;
using Trykatch.Modules.Projects.Infrastructure;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class ModuleEventDeliveryIntegrationTests
{
    private const string EventKey = "probe.committed";
    private const string ConsumerOne = "probe.effect-one";
    private const string ConsumerTwo = "probe.effect-two";

    [TestMethod]
    public async Task TwoConsumersCommitOnceWhenSecondRetries()
    {
        await using EventFixture fixture = await EventFixture.StartAsync();
        SeededEvent seeded = await fixture.SeedEventAsync();
        await fixture.FanOutAsync();
        Guid first = await fixture.DeliveryIdAsync(seeded.MessageId, ConsumerOne);
        Guid second = await fixture.DeliveryIdAsync(seeded.MessageId, ConsumerTwo);
        fixture.Behavior.FailOnceAfterWrite(ConsumerTwo);

        (await fixture.ProcessAsync(first)).State.ShouldBe(ModuleExecutionState.Completed);
        (await fixture.ProcessAsync(second)).State.ShouldBe(ModuleExecutionState.RetryableFailure);
        (await fixture.CountOutboxAsync(seeded.OrganizationId)).ShouldBe(2);
        await fixture.MakeDueAsync(second);
        (await fixture.ProcessAsync(second)).State.ShouldBe(ModuleExecutionState.Completed);

        (await fixture.ReadEffectCountAsync(seeded.OrganizationId, ConsumerOne)).ShouldBe(1);
        (await fixture.ReadEffectCountAsync(seeded.OrganizationId, ConsumerTwo)).ShouldBe(1);
        (await fixture.CountOutboxAsync(seeded.OrganizationId)).ShouldBe(3);
        (await fixture.ReadInboxCountAsync(seeded.OrganizationId, seeded.MessageId)).ShouldBe(2);
    }

    [TestMethod]
    public async Task SourceTransactionRollbackBeforeFanoutCommitLeavesNoDeliveryOrProcessedSource()
    {
        await using EventFixture fixture = await EventFixture.StartAsync();
        SeededEvent seeded = await fixture.SeedEventAsync();
        await fixture.ExecuteOwnerAsync("ALTER TABLE infrastructure.module_event_deliveries ADD CONSTRAINT ck_test_event_delivery_rollback CHECK (\"ConsumerKey\" <> 'probe.effect-two')");
        try
        {
            DbUpdateException failure = await Should.ThrowAsync<DbUpdateException>(() => fixture.FanOutAsync());
            failure.GetBaseException().ShouldBeOfType<PostgresException>().SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            (await fixture.IsSourceProcessedAsync(seeded.MessageId)).ShouldBeFalse();
            (await fixture.CountDeliveriesAsync(seeded.MessageId)).ShouldBe(0);
        }
        finally
        {
            await fixture.ExecuteOwnerAsync("ALTER TABLE infrastructure.module_event_deliveries DROP CONSTRAINT ck_test_event_delivery_rollback");
        }
    }

    [TestMethod]
    public async Task FanoutCommitSurvivesProviderRestart()
    {
        await using EventFixture fixture = await EventFixture.StartAsync();
        SeededEvent seeded = await fixture.SeedEventAsync();
        await fixture.FanOutAsync();
        await fixture.RestartAsync();

        foreach (string consumer in new[] { ConsumerOne, ConsumerTwo })
            (await fixture.ProcessAsync(await fixture.DeliveryIdAsync(seeded.MessageId, consumer))).State
                .ShouldBe(ModuleExecutionState.Completed);

        (await fixture.ReadInboxCountAsync(seeded.OrganizationId, seeded.MessageId)).ShouldBe(2);
        (await fixture.ReadEffectCountAsync(seeded.OrganizationId, ConsumerOne)).ShouldBe(1);
        (await fixture.ReadEffectCountAsync(seeded.OrganizationId, ConsumerTwo)).ShouldBe(1);
    }

    [TestMethod]
    public async Task EventEffectRollbackBeforeCommitLeavesNoEffectOrInboxMarker()
    {
        await using EventFixture fixture = await EventFixture.StartAsync();
        SeededEvent seeded = await fixture.SeedEventAsync(failAfterWrite: true);
        await fixture.FanOutAsync();
        Guid delivery = await fixture.DeliveryIdAsync(seeded.MessageId, ConsumerOne);

        (await fixture.ProcessAsync(delivery)).State.ShouldBe(ModuleExecutionState.RetryableFailure);

        (await fixture.ReadEffectCountAsync(seeded.OrganizationId, ConsumerOne)).ShouldBe(0);
        (await fixture.ReadInboxCountAsync(seeded.OrganizationId, seeded.MessageId)).ShouldBe(0);
        (await fixture.ReadDeliveryStateAsync(delivery)).ShouldBe("queued");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LostAcknowledgementRecoversFromInboxIncludingFifthAttempt(bool exhaustBusinessAttempts)
    {
        await using EventFixture fixture = await EventFixture.StartAsync();
        SeededEvent seeded = await fixture.SeedEventAsync();
        await fixture.FanOutAsync();
        Guid delivery = await fixture.DeliveryIdAsync(seeded.MessageId, ConsumerOne);
        (await fixture.ProcessAsync(delivery)).State.ShouldBe(ModuleExecutionState.Completed);
        await fixture.SimulateLostAcknowledgementAsync(delivery, exhaustBusinessAttempts ? 5 : 1);

        (await fixture.ProcessAsync(delivery)).State.ShouldBe(ModuleExecutionState.AlreadyCompleted);

        (await fixture.ReadEffectCountAsync(seeded.OrganizationId, ConsumerOne)).ShouldBe(1);
        (await fixture.ReadInboxCountAsync(seeded.OrganizationId, seeded.MessageId)).ShouldBe(1);
        (await fixture.ReadDeliveryStateAsync(delivery)).ShouldBe("completed");
    }

    [TestMethod]
    public async Task DuplicateReceiptAndCompetingWorkersDoNotRepeatEffect()
    {
        await using EventFixture fixture = await EventFixture.StartAsync();
        SeededEvent seeded = await fixture.SeedEventAsync();
        await fixture.FanOutAsync();
        (await fixture.CountDeliveriesAsync(seeded.MessageId)).ShouldBe(2);
        await fixture.FanOutAsync();
        (await fixture.CountDeliveriesAsync(seeded.MessageId)).ShouldBe(2);

        Guid delivery = await fixture.DeliveryIdAsync(seeded.MessageId, ConsumerOne);
        fixture.Behavior.HoldAfterWrite(ConsumerOne);
        Task<ModuleExecutionOutcome> first = fixture.ProcessAsync(delivery);
        await fixture.Behavior.WaitUntilHeldAsync();
        ModuleExecutionOutcome competing = await fixture.ProcessAsync(delivery);
        competing.State.ShouldBe(ModuleExecutionState.Paused);
        fixture.Behavior.ReleaseHeldConsumer();
        (await first).State.ShouldBe(ModuleExecutionState.Completed);

        (await fixture.ProcessAsync(delivery)).State.ShouldBe(ModuleExecutionState.AlreadyCompleted);
        (await fixture.ReadEffectCountAsync(seeded.OrganizationId, ConsumerOne)).ShouldBe(1);
    }

    [TestMethod]
    public async Task UnknownEventSchemaAndForgedOrganizationSnapshotAreTerminalizedWithoutEffects()
    {
        await using EventFixture fixture = await EventFixture.StartAsync();
        SeededEvent unknown = await fixture.SeedEventAsync(schemaVersion: 99);

        await fixture.FanOutAsync();

        (await fixture.ReadSourceFailureCodeAsync(unknown.MessageId)).ShouldBe(ModuleEventRegistry.InvalidSourceCode);
        (await fixture.CountDeliveriesAsync(unknown.MessageId)).ShouldBe(0);

        SeededEvent valid = await fixture.SeedEventAsync();
        await fixture.FanOutAsync();
        Guid delivery = await fixture.DeliveryIdAsync(valid.MessageId, ConsumerOne);
        await fixture.ForgeDeliveryOrganizationAsync(delivery, Guid.CreateVersion7());

        (await fixture.ProcessAsync(delivery)).State.ShouldBe(ModuleExecutionState.TerminalFailure);
        (await fixture.ReadEffectCountAsync(valid.OrganizationId, ConsumerOne)).ShouldBe(0);
        (await fixture.ReadDeliveryStateAsync(delivery)).ShouldBe("terminal");
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(0)]
    public async Task DisabledConsumerPausesAndReenabledConsumerResumes(int remainingConsumers)
    {
        await using EventFixture fixture = await EventFixture.StartAsync();
        SeededEvent seeded = await fixture.SeedEventAsync();
        await fixture.FanOutAsync();
        Guid delivery = await fixture.DeliveryIdAsync(seeded.MessageId, ConsumerTwo);

        await fixture.RestartAsync(consumerCount: remainingConsumers);
        (await fixture.ProcessAsync(delivery)).State.ShouldBe(ModuleExecutionState.Paused);
        (await fixture.ReadDeliveryStateAsync(delivery)).ShouldBe("paused");

        await fixture.RestartAsync(consumerCount: 2);
        await fixture.MakeDueAsync(delivery);
        (await fixture.ProcessAsync(delivery)).State.ShouldBe(ModuleExecutionState.Completed);
        (await fixture.ReadEffectCountAsync(seeded.OrganizationId, ConsumerTwo)).ShouldBe(1);
    }

    [TestMethod]
    public async Task NewlyInstalledConsumerDoesNotReceiveHistoricalOutboxEvents()
    {
        await using EventFixture fixture = await EventFixture.StartAsync(consumerCount: 0);
        SeededEvent seeded = await fixture.SeedEventAsync(processed: true);

        await fixture.RestartAsync(consumerCount: 2);
        await fixture.FanOutAsync();

        (await fixture.CountDeliveriesAsync(seeded.MessageId)).ShouldBe(0);
        (await fixture.ReadEffectCountAsync(seeded.OrganizationId, ConsumerOne)).ShouldBe(0);
    }

    [TestMethod]
    public async Task OldPausedConsumerDoesNotStarveEarlierReadyDelivery()
    {
        await using EventFixture fixture = await EventFixture.StartAsync();
        SeededEvent seeded = await fixture.SeedEventAsync();
        await fixture.FanOutAsync();
        Guid first = await fixture.DeliveryIdAsync(seeded.MessageId, ConsumerOne);
        Guid second = await fixture.DeliveryIdAsync(seeded.MessageId, ConsumerTwo);
        await fixture.ExecuteOwnerAsync("""
            UPDATE infrastructure.module_event_deliveries
            SET "CreatedAtUtc"=CURRENT_TIMESTAMP - interval '30 seconds', "NextAttemptAtUtc"=NULL WHERE "Id"=@first;
            UPDATE infrastructure.module_event_deliveries
            SET "State"='paused', "CreatedAtUtc"=CURRENT_TIMESTAMP - interval '7 days',
                "NextAttemptAtUtc"=CURRENT_TIMESTAMP - interval '1 second' WHERE "Id"=@second;
            """, ("first", first), ("second", second));

        (await fixture.ClaimNextAsync()).ShouldBe(first);
    }

    [TestMethod]
    public async Task CancellationRollsBackEffectsAndReleasesLease()
    {
        await using EventFixture fixture = await EventFixture.StartAsync();
        SeededEvent seeded = await fixture.SeedEventAsync();
        await fixture.FanOutAsync();
        Guid delivery = await fixture.DeliveryIdAsync(seeded.MessageId, ConsumerOne);
        fixture.Behavior.HoldAfterWrite(ConsumerOne);
        using CancellationTokenSource cancellation = new();
        Task<ModuleExecutionOutcome> execution = fixture.ProcessAsync(delivery, cancellation.Token);
        await fixture.Behavior.WaitUntilHeldAsync();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await execution);
        (await fixture.ReadEffectCountAsync(seeded.OrganizationId, ConsumerOne)).ShouldBe(0);
        (await fixture.ReadInboxCountAsync(seeded.OrganizationId, seeded.MessageId)).ShouldBe(0);
        (await fixture.CountOutboxAsync(seeded.OrganizationId)).ShouldBe(1);
        (await fixture.ReadDeliveryStateAsync(delivery)).ShouldBe("queued");
    }

    [TestMethod]
    public async Task HostedWorkerConsumesCommittedSnapshotAndStopsCleanly()
    {
        await using EventFixture fixture = await EventFixture.StartAsync();
        SeededEvent seeded = await fixture.SeedEventAsync();
        await fixture.FanOutAsync();
        await fixture.RunWorkerUntilCompletedAsync(seeded.MessageId);
        (await fixture.ReadInboxCountAsync(seeded.OrganizationId, seeded.MessageId)).ShouldBe(2);
        (await fixture.ReadEffectCountAsync(seeded.OrganizationId, ConsumerOne)).ShouldBe(1);
        (await fixture.ReadEffectCountAsync(seeded.OrganizationId, ConsumerTwo)).ShouldBe(1);
    }

    [TestMethod]
    public async Task CurrentRecipientPolicyDenialPreventsDeliveryEffect()
    {
        await using EventFixture fixture = await EventFixture.StartAsync();
        SeededEvent seeded = await fixture.SeedEventAsync(canDisclose: true);
        await fixture.FanOutAsync();
        await fixture.SetDisclosureAsync(seeded.OrganizationId, seeded.RecordId, false);
        Guid delivery = await fixture.DeliveryIdAsync(seeded.MessageId, ConsumerOne);

        (await fixture.ProcessAsync(delivery)).State.ShouldBe(ModuleExecutionState.Denied);

        (await fixture.ReadEffectCountAsync(seeded.OrganizationId, ConsumerOne)).ShouldBe(0);
        (await fixture.ReadDeliveryStateAsync(delivery)).ShouldBe("terminal");
        string safe = await fixture.ReadDeliveryFailureCodeAsync(delivery);
        safe.ShouldNotBeNullOrEmpty();
        safe.ShouldNotContain("recipient");
    }

    private sealed class EventFixture(PostgreSqlContainer postgres, string owner, IConfiguration runtimeConfiguration,
        EventProbeBehavior behavior, int consumerCount) : IAsyncDisposable
    {
        private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
        private ServiceProvider services = null!;
        private int activeConsumerCount = consumerCount;
        private readonly string organizationConnection = RoleConnection(owner, PostgresRuntimeRoleFixture.OrganizationRole,
            PostgresRuntimeRoleFixture.OrganizationPassword);

        public EventProbeBehavior Behavior { get; } = behavior;

        public static async Task<EventFixture> StartAsync(int consumerCount = 2)
        {
            PostgreSqlContainer postgres = new PostgreSqlBuilder(
                "postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f").Build();
            await postgres.StartAsync();
            string owner = postgres.GetConnectionString();
            await PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(owner);

            IConfiguration configuration = new ConfigurationBuilder().Build();
            IModule[] baselineModules = [new ProjectsModule(), new DocumentsModule()];
            ServiceCollection baselineServices = new();
            foreach (IModule module in baselineModules) module.Register(baselineServices, configuration);
            using ServiceProvider baselineProvider = baselineServices.BuildServiceProvider();
            IApplicationModelContributor[] baselineContributors = baselineProvider.GetServices<IApplicationModelContributor>().ToArray();
            await using (IdentityDbContext identity = new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(owner).Options))
                await identity.Database.MigrateAsync();
            await using (PlatformDbContext platform = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(owner).Options))
                await platform.Database.MigrateAsync();
            ModuleCatalog baselineCatalog = new(baselineModules);
            await using (ApplicationDbContext application = new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(owner).Options,
                             baselineContributors, moduleCatalog: baselineCatalog))
            {
                application.Database.HasPendingModelChanges().ShouldBeFalse();
                await application.Database.MigrateAsync();
            }
            await ApplyModuleMigrationsAsync(owner, baselineModules);
            await CreateProbeTableAsync(owner);

            ProbeModule probe = new(consumerCount);
            IModule[] modules = [.. baselineModules, probe];
            ModuleCatalog catalog = new(modules);
            await InstalledSchemaCatalog.SynchronizeAsync(owner, catalog.Descriptors.SelectMany(module => module.DataResources
                .Select(resource => new InstalledDataResource(module.Id, resource))));
            await RuntimeRoleProvisioner.ProvisionAsync(owner, RuntimeRoles());
            (string organization, string platformRuntime, string identityRuntime, string outbox) =
                await PostgresRuntimeRoleFixture.CreateConnectionStringsAsync(owner);
            string coordinator = RoleConnection(owner, PostgresRuntimeRoleFixture.WorkCoordinatorRole,
                PostgresRuntimeRoleFixture.WorkCoordinatorPassword);
            Dictionary<string, string?> settings = new()
            {
                [$"ConnectionStrings:{RuntimeDatabaseConnectionContract.Organization}"] = organization,
                [$"ConnectionStrings:{RuntimeDatabaseConnectionContract.Platform}"] = platformRuntime,
                [$"ConnectionStrings:{RuntimeDatabaseConnectionContract.Identity}"] = identityRuntime,
                [$"ConnectionStrings:{RuntimeDatabaseConnectionContract.Outbox}"] = outbox,
                [$"ConnectionStrings:{RuntimeDatabaseConnectionContract.WorkCoordinator}"] = coordinator,
                ["Email:Host"] = "127.0.0.1",
                ["Email:Port"] = "1",
                ["Email:From"] = "event-test@example.test",
                ["Email:Security"] = "StartTls"
            };
            IConfiguration runtimeConfiguration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            EventFixture fixture = new(postgres, owner, runtimeConfiguration, new EventProbeBehavior(), consumerCount);
            fixture.services = fixture.CreateServices(consumerCount);
            return fixture;
        }

        public async Task RestartAsync(int? consumerCount = null)
        {
            if (consumerCount is { } count) activeConsumerCount = count;
            await services.DisposeAsync();
            services = CreateServices(activeConsumerCount);
        }

        private ServiceProvider CreateServices(int activeConsumers)
        {
            ProbeModule probe = new(activeConsumers);
            IModule[] modules = [new ProjectsModule(), new DocumentsModule(), probe];
            ServiceCollection services = new();
            services.AddSingleton(runtimeConfiguration);
            services.AddLogging();
            services.AddSingleton(Behavior);
            services.AddApplication();
            services.AddModules(runtimeConfiguration, modules);
            services.AddInfrastructure(runtimeConfiguration);
            return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        }

        public async Task<SeededEvent> SeedEventAsync(int schemaVersion = 1, bool canDisclose = true,
            bool failAfterWrite = false, bool processed = false)
        {
            SeededOrganization seeded = await SeedOrganizationAsync();
            Guid record = Guid.CreateVersion7();
            await ExecuteOwnerAsync("""
                INSERT INTO app.module_event_probe_records ("Id","OrganizationId","RecipientActorId","CanDisclose","EffectOne","EffectTwo")
                VALUES (@id,@organization,@recipient,@allowed,0,0)
                """, ("id", record), ("organization", seeded.OrganizationId), ("recipient", seeded.ActorId), ("allowed", canDisclose));
            Guid message = Guid.CreateVersion7();
            string payload = JsonSerializer.Serialize(new EventPayload(record, seeded.ActorId, failAfterWrite), WebJson);
            ModuleCatalog catalog = services.GetRequiredService<ModuleCatalog>();
            OutboxContext context = new(seeded.OrganizationId, seeded.ActorId, seeded.ActorId, seeded.MembershipId,
                "UserCommand", "probe.publish", 1, EventKey, schemaVersion, OutboxCatalogVersion.Compute(catalog), null);
            await using ApplicationDbContext db = new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(owner).Options);
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = message,
                Type = EventKey,
                Payload = payload,
                ContextVersion = 1,
                ContextJson = JsonSerializer.Serialize(context, WebJson),
                PayloadHash = OutboxPayloadHash.Compute(payload),
                ProcessedAt = processed ? DateTimeOffset.UtcNow : null
            });
            await db.SaveChangesAsync();
            return new(seeded.OrganizationId, seeded.ActorId, message, record);
        }

        public async Task FanOutAsync()
        {
            await using AsyncServiceScope scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(CancellationToken.None);
        }

        public async Task<ModuleExecutionOutcome> ProcessAsync(Guid deliveryId, CancellationToken cancellationToken = default)
        {
            await using AsyncServiceScope scope = services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ModuleEventDeliveryProcessor>()
                .ProcessAsync(deliveryId, cancellationToken);
        }

        public async Task<Guid> ClaimNextAsync()
        {
            ModuleEventDeliveryClaim? claim = await services.GetRequiredService<IModuleEventDeliveryStore>().ClaimAsync(null, CancellationToken.None);
            return claim!.Delivery!.Id;
        }

        public async Task RunWorkerUntilCompletedAsync(Guid messageId)
        {
            ModuleEventDeliveryWorker worker = services.GetServices<IHostedService>().OfType<ModuleEventDeliveryWorker>().Single();
            await worker.StartAsync(CancellationToken.None);
            try
            {
                using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
                while (await CountOwnerAsync("SELECT count(*) FROM infrastructure.module_event_deliveries WHERE \"MessageId\"=@message AND \"State\"='completed'", ("message", messageId)) != 2)
                    await Task.Delay(50, deadline.Token);
            }
            finally
            {
                using CancellationTokenSource shutdown = new(TimeSpan.FromSeconds(5));
                await worker.StopAsync(shutdown.Token);
            }
        }

        public async Task<Guid> DeliveryIdAsync(Guid messageId, string consumerKey) => Guid.Parse(
            (string)(await ScalarOwnerAsync("SELECT \"Id\"::text FROM infrastructure.module_event_deliveries WHERE \"MessageId\"=@message AND \"ConsumerKey\"=@consumer",
                ("message", messageId), ("consumer", consumerKey)))!);

        public Task<int> CountDeliveriesAsync(Guid messageId) => CountOwnerAsync(
            "SELECT count(*) FROM infrastructure.module_event_deliveries WHERE \"MessageId\"=@message", ("message", messageId));

        public async Task<bool> IsSourceProcessedAsync(Guid messageId) => (bool)(await ScalarOwnerAsync(
            "SELECT EXISTS(SELECT 1 FROM platform.outbox_messages WHERE \"Id\"=@message AND \"ProcessedAt\" IS NOT NULL)", ("message", messageId)))!;

        public Task<int> ReadEffectCountAsync(Guid organizationId, string consumerKey) => CountOwnerAsync(
            $"SELECT COALESCE(sum(\"{(consumerKey == ConsumerOne ? "EffectOne" : "EffectTwo")}\"),0) FROM app.module_event_probe_records WHERE \"OrganizationId\"=@organization",
            ("organization", organizationId));

        public Task<int> ReadInboxCountAsync(Guid organizationId, Guid messageId) => CountOwnerAsync(
            "SELECT count(*) FROM app.module_event_inbox WHERE \"OrganizationId\"=@organization AND \"MessageId\"=@message",
            ("organization", organizationId), ("message", messageId));

        public Task<int> CountOutboxAsync(Guid organizationId) => CountOwnerAsync(
            "SELECT count(*) FROM platform.outbox_messages WHERE \"ContextJson\"->>'organizationId'=@organization",
            ("organization", organizationId.ToString()));

        public async Task<string> ReadDeliveryStateAsync(Guid deliveryId) => (string)(await ScalarOwnerAsync(
            "SELECT \"State\" FROM infrastructure.module_event_deliveries WHERE \"Id\"=@id", ("id", deliveryId)))!;

        public async Task<string> ReadDeliveryFailureCodeAsync(Guid deliveryId) => (string)(await ScalarOwnerAsync(
            "SELECT \"LastFailureCode\" FROM infrastructure.module_event_deliveries WHERE \"Id\"=@id", ("id", deliveryId)))!;

        public async Task<string?> ReadSourceFailureCodeAsync(Guid messageId) => (string?)(await ScalarOwnerAsync(
            "SELECT \"LastErrorCode\" FROM platform.outbox_messages WHERE \"Id\"=@message", ("message", messageId)))!;

        public Task SetDisclosureAsync(Guid organizationId, Guid recordId, bool allowed) => ExecuteOwnerAsync(
            "UPDATE app.module_event_probe_records SET \"CanDisclose\"=@allowed WHERE \"OrganizationId\"=@organization AND \"Id\"=@id",
            ("allowed", allowed), ("organization", organizationId), ("id", recordId));

        public Task ForgeDeliveryOrganizationAsync(Guid deliveryId, Guid organizationId) => ExecuteOwnerAsync(
            "UPDATE infrastructure.module_event_deliveries SET \"OrganizationId\"=@organization WHERE \"Id\"=@id",
            ("organization", organizationId), ("id", deliveryId));

        public Task MakeDueAsync(Guid deliveryId) => ExecuteOwnerAsync(
            "UPDATE infrastructure.module_event_deliveries SET \"NextAttemptAtUtc\"=CURRENT_TIMESTAMP - interval '1 second', \"LeaseUntilUtc\"=NULL WHERE \"Id\"=@id",
            ("id", deliveryId));

        public Task SimulateLostAcknowledgementAsync(Guid deliveryId, int attempts) => ExecuteOwnerAsync(
            "UPDATE infrastructure.module_event_deliveries SET \"State\"='queued', \"Attempts\"=@attempts, \"NextAttemptAtUtc\"=CURRENT_TIMESTAMP - interval '1 second', \"LeaseId\"=NULL, \"LeaseUntilUtc\"=NULL WHERE \"Id\"=@id",
            ("attempts", attempts), ("id", deliveryId));

        private async Task<SeededOrganization> SeedOrganizationAsync()
        {
            Organization organization = Organization.Create("Module event execution", $"event-{Guid.NewGuid():N}"[..30]);
            Guid actor = Guid.CreateVersion7();
            Role role = Role.Create(organization.Id, "Event actor", isSystem: true);
            Membership membership = Membership.Create(organization.Id, actor);
            membership.AssignRole(role.Id);
            OrganizationDataPlacementRecord placement = OrganizationDataPlacementRecord.Begin(
                organization.Id, OrganizationDataPlacementKind.Shared, "postgres", null);
            placement.MarkReady(new NpgsqlConnectionStringBuilder(owner).Database!, "integration-test", "e3", DateTimeOffset.UtcNow);
            await using PlatformDbContext db = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(owner).Options);
            db.AddRange(organization, role, membership, placement);
            await db.SaveChangesAsync();
            return new(organization.Id, actor, membership.Id);
        }

        public Task ExecuteOwnerAsync(string sql, params (string Name, object Value)[] parameters) => ExecuteOwnerCoreAsync(sql, parameters);

        private async Task ExecuteOwnerCoreAsync(string sql, (string Name, object Value)[] parameters)
        {
            await using NpgsqlConnection connection = new(owner);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new(sql, connection);
            foreach ((string name, object value) in parameters) command.Parameters.AddWithValue(name, value);
            await command.ExecuteNonQueryAsync();
        }

        private async Task<int> CountOwnerAsync(string sql, params (string Name, object Value)[] parameters) =>
            Convert.ToInt32(await ScalarOwnerAsync(sql, parameters), CultureInfo.InvariantCulture);

        private async Task<object?> ScalarOwnerAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using NpgsqlConnection connection = new(owner);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new(sql, connection);
            foreach ((string name, object value) in parameters) command.Parameters.AddWithValue(name, value);
            return await command.ExecuteScalarAsync();
        }

        private static async Task CreateProbeTableAsync(string owner)
        {
            await using NpgsqlConnection connection = new(owner);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("""
                CREATE TABLE app.module_event_probe_records (
                    "Id" uuid PRIMARY KEY,
                    "OrganizationId" uuid NOT NULL,
                    "RecipientActorId" uuid NOT NULL,
                    "CanDisclose" boolean NOT NULL,
                    "EffectOne" integer NOT NULL DEFAULT 0,
                    "EffectTwo" integer NOT NULL DEFAULT 0);
                CREATE INDEX module_event_probe_records_organization_id ON app.module_event_probe_records ("OrganizationId", "Id");
                ALTER TABLE app.module_event_probe_records ENABLE ROW LEVEL SECURITY;
                ALTER TABLE app.module_event_probe_records FORCE ROW LEVEL SECURITY;
                CREATE POLICY module_event_probe_records_organization ON app.module_event_probe_records
                    USING ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid)
                    WITH CHECK ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid);
                """, connection);
            await command.ExecuteNonQueryAsync();
        }

        private static async Task ApplyModuleMigrationsAsync(string owner, IReadOnlyList<IModule> modules)
        {
            foreach (PendingModuleMigration migration in ModuleMigrationPlan.Build(modules, []))
            {
                await using NpgsqlConnection connection = new(owner);
                await connection.OpenAsync();
                await using NpgsqlCommand command = new(migration.Sql, connection);
                await command.ExecuteNonQueryAsync();
            }
        }

        private static RuntimeDatabaseRoles RuntimeRoles() => new(PostgresRuntimeRoleFixture.OrganizationRole,
            PostgresRuntimeRoleFixture.PlatformRole, PostgresRuntimeRoleFixture.IdentityRole,
            PostgresRuntimeRoleFixture.OutboxRole, PostgresRuntimeRoleFixture.WorkCoordinatorRole);

        private static string RoleConnection(string owner, string role, string password) =>
            new NpgsqlConnectionStringBuilder(owner) { Username = role, Password = password }.ConnectionString;

        public async ValueTask DisposeAsync()
        {
            if (services is not null) await services.DisposeAsync();
            await postgres.DisposeAsync();
        }
    }

    private sealed class EventProbeBehavior
    {
        private readonly ConcurrentDictionary<string, int> failures = new(StringComparer.Ordinal);
        private readonly object gateLock = new();
        private string? gatedConsumer;
        private TaskCompletionSource? reached;
        private TaskCompletionSource? release;

        public void FailOnceAfterWrite(string consumerKey) => failures[consumerKey] = 1;

        public void HoldAfterWrite(string consumerKey)
        {
            lock (gateLock)
            {
                gatedConsumer = consumerKey;
                reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
                release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public async Task AfterWriteAsync(string consumerKey, CancellationToken cancellationToken)
        {
            Task? wait = null;
            lock (gateLock)
            {
                if (gatedConsumer == consumerKey)
                {
                    reached!.TrySetResult();
                    wait = release!.Task;
                }
            }
            if (wait is not null) await wait.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            if (failures.TryGetValue(consumerKey, out int remaining) && remaining > 0
                && failures.TryUpdate(consumerKey, remaining - 1, remaining))
                throw new InvalidOperationException("test-consumer-failure");
        }

        public Task WaitUntilHeldAsync()
        {
            lock (gateLock) return reached!.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public void ReleaseHeldConsumer()
        {
            lock (gateLock)
            {
                gatedConsumer = null;
                release!.TrySetResult();
            }
        }
    }

    private sealed class ProbeModule(int consumerCount) : IModule
    {
        private static readonly Guid AuditActor = Guid.Parse("991aa0b3-fad2-4201-8a3d-9cc6d6450261");
        private const string Grant = "probe.event-effects";
        private const string Resource = "probe.event-records";
        private static readonly string[] NoPermissions = [];
        private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

        public ModuleDescriptor Descriptor { get; } = new(
            "probe", "Module event execution probe", "1.0.0", "Real database event delivery integration fixture.", [], [],
            ModuleCapabilities.Api | ModuleCapabilities.BackgroundWork | ModuleCapabilities.Data, [])
        {
            DefaultDataOwnership = ModuleDataOwnership.Organization,
            DataResources = [new(Resource, "app", "module_event_probe_records", ModuleDataOwnership.Organization,
                typeof(ProbeRecord).FullName, "module_event_probe_records_organization")],
            Operations = OperationsFor(consumerCount),
            SystemGrants = consumerCount > 0 ? [new(Grant, 1, "probe", AuditActor, ["probe.apply"], [Resource])] : [],
            IntegrationEvents =
            [
                new(EventKey, 1, "probe", typeof(JsonElement), element => element, 4096),
                new("probe.followup", 1, "probe", typeof(ProbeFollowup), element => element.Deserialize<ProbeFollowup>(PayloadJson)!, 4096)
            ],
            EventConsumers = Consumers(consumerCount)
        };

        public void Register(IServiceCollection services, IConfiguration configuration)
        {
            services.AddScoped<IApplicationModelContributor, ProbeModelContributor>();
            services.AddScoped<IModuleOperationResourcePolicy, ProbeResourcePolicy>();
            services.AddScoped<IModuleOperationHandler, ProbePublishHandler>();
            if (consumerCount > 0)
                services.AddScoped<ModuleEventConsumerBinding>(provider => new("probe", ConsumerOne,
                    new ProbeEventConsumer(ConsumerOne, provider.GetRequiredService<ApplicationDbContext>(), provider.GetRequiredService<EventProbeBehavior>(), provider.GetRequiredService<IOutboxWriter>())));
            if (consumerCount > 1)
                services.AddScoped<ModuleEventConsumerBinding>(provider => new("probe", ConsumerTwo,
                    new ProbeEventConsumer(ConsumerTwo, provider.GetRequiredService<ApplicationDbContext>(), provider.GetRequiredService<EventProbeBehavior>(), provider.GetRequiredService<IOutboxWriter>())));
        }

        private static List<ModuleOperationDescriptor> OperationsFor(int count)
        {
            List<ModuleOperationDescriptor> operations =
            [new("probe", "probe.publish", 1, ModuleExecutionKind.UserCommand, "probe.publish.payload", 1, NoPermissions, Resource, null, 4096)];
            if (count > 0) operations.Add(new("probe", "probe.apply", 1, ModuleExecutionKind.CommittedEvent, "probe.apply.payload", 1,
                NoPermissions, Resource, Grant, 4096));
            return operations;
        }

        private sealed class ProbePublishHandler(IOutboxWriter outbox) : IModuleOperationHandler
        {
            public string ModuleId => "probe";
            public string OperationKey => "probe.publish";
            public int OperationVersion => 1;
            public Task HandleAsync(ModuleOperationContext context, JsonElement payload, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                outbox.Enqueue(payload.Clone(), OperationKey, OperationVersion);
                return Task.CompletedTask;
            }
        }

        private static List<ModuleEventConsumerDescriptor> Consumers(int count)
        {
            List<ModuleEventConsumerDescriptor> consumers = [];
            if (count > 0) consumers.Add(new(ConsumerOne, "probe", [new(EventKey, 1)], "probe.apply", 1, Grant));
            if (count > 1) consumers.Add(new(ConsumerTwo, "probe", [new(EventKey, 1)], "probe.apply", 1, Grant));
            return consumers;
        }

        private sealed class ProbeModelContributor : IApplicationModelContributor
        {
            public string ModuleId => "probe";
            public void Configure(ModelBuilder modelBuilder) => modelBuilder.Entity<ProbeRecord>(entity =>
            {
                entity.ToTable("module_event_probe_records", "app");
                entity.HasKey(record => record.Id);
                entity.HasIndex(record => new { record.OrganizationId, record.Id });
            });
        }

        private sealed class ProbeResourcePolicy(ApplicationDbContext db) : IModuleOperationResourcePolicy
        {
            public string ModuleId => "probe";
            public string PolicyKey => Resource;
            public async Task<bool> AuthorizeAsync(ModuleOperationContext context, JsonElement payload, CancellationToken cancellationToken)
            {
                EventPayload? request = payload.Deserialize<EventPayload>(PayloadJson);
                if (request is null) return false;
                ProbeRecord? record = await db.Set<ProbeRecord>().AsNoTracking()
                    .SingleOrDefaultAsync(row => row.Id == request.RecordId, cancellationToken);
                return record is not null && record.CanDisclose && record.RecipientActorId == request.RecipientActorId;
            }
        }
    }

    private sealed class ProbeEventConsumer(string consumerKey, ApplicationDbContext db, EventProbeBehavior behavior, IOutboxWriter outbox) : IModuleEventConsumer
    {
        private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

        public async Task HandleAsync(ModuleEventContext context, JsonElement payload, CancellationToken cancellationToken)
        {
            EventPayload data = payload.Deserialize<EventPayload>(PayloadJson)!;
            if (consumerKey == ConsumerOne)
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE app.module_event_probe_records SET \"EffectOne\"=\"EffectOne\"+1 WHERE \"Id\"={data.RecordId}", cancellationToken);
            }
            else
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE app.module_event_probe_records SET \"EffectTwo\"=\"EffectTwo\"+1 WHERE \"Id\"={data.RecordId}", cancellationToken);
            }
            outbox.Enqueue(new ProbeFollowup(data.RecordId, consumerKey), "probe.apply", 1);
            await db.SaveChangesAsync(cancellationToken);
            await behavior.AfterWriteAsync(consumerKey, cancellationToken);
            if (data.FailAfterWrite) throw new InvalidOperationException("test-consumer-failure");
        }
    }

    private sealed class ProbeRecord : IOrganizationOwned
    {
        public Guid Id { get; set; }
        public Guid OrganizationId { get; set; }
        public Guid RecipientActorId { get; set; }
        public bool CanDisclose { get; set; }
        public int EffectOne { get; set; }
        public int EffectTwo { get; set; }
    }

    private sealed record ProbeFollowup(Guid RecordId, string ConsumerKey);
    private sealed record EventPayload(Guid RecordId, Guid RecipientActorId, bool FailAfterWrite);
    private sealed record SeededEvent(Guid OrganizationId, Guid ActorId, Guid MessageId, Guid RecordId);
    private sealed record SeededOrganization(Guid OrganizationId, Guid ActorId, Guid MembershipId);
}
