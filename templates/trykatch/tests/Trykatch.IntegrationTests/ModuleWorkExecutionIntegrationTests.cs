using System.Text.Json;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Application;
using Trykatch.Application.Execution;
using Trykatch.Application.Outbox;
using Trykatch.Domain.Organizations;
using Trykatch.Identity;
using Trykatch.Infrastructure;
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
public sealed class ModuleWorkExecutionIntegrationTests
{
    private static ExecutionFixture fixture = null!;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext _)
    {
        fixture = await ExecutionFixture.StartAsync();
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (fixture is not null)
            await fixture.DisposeAsync();
    }

    [TestMethod]
    public async Task RevokedUserCommandDoesNotExecute()
    {
        SeededWork work = await fixture.SeedUserWorkAsync("probe.command");
        (await fixture.RevokeMembershipAsync(work.OrganizationId, work.OriginActorId)).ShouldBe(1);

        ModuleExecutionOutcome outcome = await fixture.ExecuteAsync(work);

        outcome.State.ShouldBe(ModuleExecutionState.Denied);
        (await fixture.CountEffectsAsync(work.OrganizationId)).ShouldBe(0);
        string audit = await fixture.ReadAuditIntentsAsync(work.OrganizationId);
        audit.ShouldContain("module.work.denied");
        audit.ShouldContain("membership_required");
        audit.ShouldNotContain("private-payload-marker");
    }

    [TestMethod]
    public async Task CommittedEventSurvivesOriginMembershipRevocation()
    {
        SeededWork work = await fixture.SeedCommittedEventWorkAsync();
        (await fixture.RevokeMembershipAsync(work.OrganizationId, work.OriginActorId)).ShouldBe(1);

        ModuleExecutionOutcome outcome = await fixture.ExecuteAsync(work);

        outcome.State.ShouldBe(ModuleExecutionState.Completed);
        (await fixture.CountEffectsAsync(work.OrganizationId)).ShouldBe(1);
        (await fixture.ReadCompletionAndAuditAsync(work)).ShouldContain(work.OriginActorId.ToString());
        work.EffectiveActorId.ShouldBe(ProbeModule.AuditActorId);
        (await fixture.ReadCompletionAndAuditAsync(work)).ShouldContain(ProbeModule.AuditActorId.ToString());
        (await fixture.ReadOutboxCountAsync(work.OrganizationId)).ShouldBe(2); // source plus one handler follow-up
    }

    [TestMethod]
    public async Task MembershipResolutionBeginsInsideActorScopedTransaction()
    {
        SeededWork work = await fixture.SeedUserWorkAsync("probe.command");

        ModuleExecutionOutcome outcome = await fixture.ExecuteAsync(work);

        outcome.State.ShouldBe(ModuleExecutionState.Completed);
        (await fixture.CountEffectsAsync(work.OrganizationId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task SystemGrantCannotRunAnotherOperation()
    {
        SeededWork work = await fixture.SeedScheduledWorkAsync(grantKey: "probe.effects");

        ModuleExecutionOutcome outcome = await fixture.ExecuteAsync(work);

        outcome.State.ShouldBe(ModuleExecutionState.Denied);
        (await fixture.CountEffectsAsync(work.OrganizationId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task MissingContextCannotReadOrganizationRows()
    {
        SeededWork work = await fixture.SeedUserWorkAsync("probe.command");
        await fixture.SeedRecordAsync(work.OrganizationId, work.ActorId, canDisclose: true);

        (await fixture.CountAsOrganizationRuntimeWithoutContextAsync()).ShouldBe(0);
    }

    [TestMethod]
    public async Task OrganizationAJobCannotMutateB()
    {
        SeededWork work = await fixture.SeedScheduledWorkAsync();
        Guid organizationB = await fixture.CreateOrganizationAsync();
        Guid recordB = await fixture.SeedRecordAsync(organizationB, work.ActorId, canDisclose: true, marker: "B-original");

        await fixture.SetTargetRecordAsync(work, recordB);
        ModuleExecutionOutcome outcome = await fixture.ExecuteAsync(work);

        outcome.State.ShouldBe(ModuleExecutionState.Completed);
        (await fixture.ReadMarkerAsync(organizationB, recordB)).ShouldBe("B-original");
    }

    [TestMethod]
    public async Task PooledConnectionDoesNotRetainPriorScope()
    {
        SeededWork work = await fixture.SeedUserWorkAsync("probe.command");
        Guid record = await fixture.SeedRecordAsync(work.OrganizationId, work.ActorId, canDisclose: true);

        await fixture.TouchOrganizationScopeOnPooledConnectionAsync(work.OrganizationId, work.ActorId);

        (await fixture.CountAsOrganizationRuntimeWithoutContextAsync()).ShouldBe(0);
        (await fixture.ReadMarkerAsync(work.OrganizationId, record)).ShouldBe("seed");
    }

    [TestMethod]
    public async Task DeniedRecipientIsNotDisclosedByEventEffect()
    {
        SeededWork work = await fixture.SeedCommittedEventWorkAsync(canDisclose: false);

        ModuleExecutionOutcome outcome = await fixture.ExecuteAsync(work);

        outcome.State.ShouldBe(ModuleExecutionState.Denied);
        (await fixture.CountEffectsAsync(work.OrganizationId)).ShouldBe(0);
        (await fixture.ReadOutboxCountAsync(work.OrganizationId)).ShouldBe(1);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HandlerFailureAndCancellationRollBackEffectCompletionAuditAndOutgoingIntent(bool cancel)
    {
        SeededWork work = await fixture.SeedUserWorkAsync("probe.command", failAfterWrites: true, cancel: cancel);
        long auditBefore = await fixture.CountAuditIntentsAsync(work.OrganizationId);

        if (cancel)
            await Should.ThrowAsync<OperationCanceledException>(() => fixture.ExecuteCancelledAsync(work));
        else
            (await fixture.ExecuteAsync(work)).State.ShouldBe(ModuleExecutionState.RetryableFailure);

        (await fixture.CountEffectsAsync(work.OrganizationId)).ShouldBe(0);
        (await fixture.ReadCompletionCountAsync(work.OrganizationId, work.WorkId)).ShouldBe(0);
        (await fixture.CountAuditIntentsAsync(work.OrganizationId)).ShouldBe(auditBefore);
        (await fixture.ReadOutboxCountAsync(work.OrganizationId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task DuplicateAndCompetingExecutionRunOnceAndRetryAfterUncertainAcknowledgement()
    {
        SeededWork work = await fixture.SeedUserWorkAsync("probe.command", holdHandler: true);
        Task<ModuleExecutionOutcome> first = fixture.ExecuteAsync(work);
        await fixture.HandlerReachedAsync();
        ModuleExecutionOutcome competing = await fixture.ExecuteAsync(work);
        competing.State.ShouldBe(ModuleExecutionState.Paused);
        fixture.ReleaseHandler();
        (await first).State.ShouldBe(ModuleExecutionState.Completed);

        // Requeue the row to model a lost acknowledgement after the database commit.
        await fixture.SimulateUncertainAcknowledgementAsync(work);
        (await fixture.ExecuteAsync(work)).State.ShouldBe(ModuleExecutionState.AlreadyCompleted);
        (await fixture.CountEffectsAsync(work.OrganizationId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task CompletionRecoverySurvivesRemovedModuleAndGrant()
    {
        SeededWork work = await fixture.SeedCommittedEventWorkAsync();
        (await fixture.ExecuteAsync(work)).State.ShouldBe(ModuleExecutionState.Completed);
        await fixture.SimulateUncertainAcknowledgementAsync(work);
        (await fixture.ExecuteWithoutProbeModuleAsync(work)).State.ShouldBe(ModuleExecutionState.AlreadyCompleted);
        (await fixture.CountEffectsAsync(work.OrganizationId)).ShouldBe(1);
        (await fixture.ReadOutboxCountAsync(work.OrganizationId)).ShouldBe(2);
    }

    [TestMethod]
    public async Task ExpectedOperationMismatchDoesNotPoisonLegitimateWork()
    {
        SeededWork work = await fixture.SeedUserWorkAsync("probe.command");
        (await fixture.ExecuteAsync(work, "probe.scheduled")).FailureCode.ShouldBe("request_mismatch");
        (await fixture.ExecuteAsync(work)).State.ShouldBe(ModuleExecutionState.Completed);
        (await fixture.CountEffectsAsync(work.OrganizationId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task StaleLeaseCannotAcknowledgeAnotherClaim()
    {
        SeededWork work = await fixture.SeedUserWorkAsync("probe.command");
        ModuleWorkClaim first = await fixture.ClaimAsync(work);
        await fixture.ExpireLeaseAsync(work);
        ModuleWorkClaim second = await fixture.ClaimAsync(work);
        second.LeaseId.ShouldNotBe(first.LeaseId);
        (await fixture.Store.FinishAsync(work.WorkId, first.LeaseId!.Value,
            new ModuleExecutionOutcome(ModuleExecutionState.TerminalFailure), CancellationToken.None)).ShouldBeFalse();
        await fixture.Store.ReleaseAsync(work.WorkId, second.LeaseId!.Value, CancellationToken.None);
        (await fixture.ExecuteAsync(work)).State.ShouldBe(ModuleExecutionState.Completed);
    }

    [TestMethod]
    public async Task RetryBackoffPreventsImmediateAnotherAttempt()
    {
        SeededWork work = await fixture.SeedUserWorkAsync("probe.command");
        ModuleWorkClaim claim = await fixture.ClaimAsync(work);
        (await fixture.Store.FinishAsync(work.WorkId, claim.LeaseId!.Value,
            new ModuleExecutionOutcome(ModuleExecutionState.RetryableFailure, "probe_retry", DateTimeOffset.UtcNow.AddMinutes(1)),
            CancellationToken.None)).ShouldBeTrue();
        (await fixture.ExecuteAsync(work)).State.ShouldBe(ModuleExecutionState.Paused);
        (await fixture.CountEffectsAsync(work.OrganizationId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task FinalAttemptAcknowledgementRecoveryDoesNotRequireCurrentMembership()
    {
        SeededWork work = await fixture.SeedUserWorkAsync("probe.command");
        await fixture.SetAttemptsAsync(work, ModuleWorkStore.MaximumAttempts - 1);
        (await fixture.ExecuteAsync(work)).State.ShouldBe(ModuleExecutionState.Completed);
        await fixture.SimulateUncertainAcknowledgementAsync(work);
        (await fixture.RevokeMembershipAsync(work.OrganizationId, work.OriginActorId)).ShouldBe(1);

        (await fixture.ExecuteAsync(work)).State.ShouldBe(ModuleExecutionState.AlreadyCompleted);
        (await fixture.CountEffectsAsync(work.OrganizationId)).ShouldBe(1);
        (await fixture.ReadOutboxCountAsync(work.OrganizationId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task ExhaustedWorkWithoutCompletionNeverRunsAnotherHandler()
    {
        SeededWork work = await fixture.SeedUserWorkAsync("probe.command");
        await fixture.SetAttemptsAsync(work, ModuleWorkStore.MaximumAttempts);
        ModuleExecutionOutcome outcome = await fixture.ExecuteAsync(work);
        outcome.State.ShouldBe(ModuleExecutionState.TerminalFailure);
        outcome.FailureCode.ShouldBe("attempts_exhausted");
        (await fixture.CountEffectsAsync(work.OrganizationId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task RepeatedOrganizationPausesDoNotConsumeFailureBudget()
    {
        SeededWork work = await fixture.SeedUserWorkAsync("probe.command");
        await fixture.SetOrganizationActiveAsync(work, false);
        for (int attempt = 0; attempt < ModuleWorkStore.MaximumAttempts + 1; attempt++)
            (await fixture.ExecuteAsync(work)).State.ShouldBe(ModuleExecutionState.Paused);
        await fixture.SetOrganizationActiveAsync(work, true);
        (await fixture.ExecuteAsync(work)).State.ShouldBe(ModuleExecutionState.Completed);
        (await fixture.CountEffectsAsync(work.OrganizationId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task GrantAuthorizedEventEffectStillRequiresCurrentResourceDisclosure()
    {
        SeededWork work = await fixture.SeedCommittedEventWorkAsync(canDisclose: true);
        await fixture.SetRecordDisclosureAsync(work.OrganizationId, work.PayloadRecordId, allowed: false);

        ModuleExecutionOutcome outcome = await fixture.ExecuteAsync(work);

        outcome.State.ShouldBe(ModuleExecutionState.Denied);
        (await fixture.CountEffectsAsync(work.OrganizationId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task BaselineApplicationModelMatchesMigrationsWithoutProbeContributor()
    {
        await fixture.AssertBaselineModelStableAsync();
    }

    private sealed class ExecutionFixture(PostgreSqlContainer postgres, string owner, ServiceProvider services) : IAsyncDisposable
    {
        private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
        private readonly string organizationConnection = RoleConnection(owner, PostgresRuntimeRoleFixture.OrganizationRole, PostgresRuntimeRoleFixture.OrganizationPassword);
        private readonly ProbeExecutionBehavior behavior = services.GetRequiredService<ProbeExecutionBehavior>();

        public static async Task<ExecutionFixture> StartAsync()
        {
            PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18.6-alpine@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f").Build();
            await postgres.StartAsync();
            string owner = postgres.GetConnectionString();
            await PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(owner);

            ProbeModule probe = new();
            IModule[] modules = [new ProjectsModule(), new DocumentsModule(), probe];
            IConfiguration configuration = new ConfigurationBuilder().Build();
            ServiceCollection moduleServices = new();
            ModuleCatalog catalog = new(modules);
            foreach (IModule module in catalog.Modules)
                module.Register(moduleServices, configuration);
            using ServiceProvider moduleProvider = moduleServices.BuildServiceProvider();
            IApplicationModelContributor[] contributors = moduleProvider.GetServices<IApplicationModelContributor>().ToArray();

            await using (IdentityDbContext identity = new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(owner).Options))
                await identity.Database.MigrateAsync();
            await using (PlatformDbContext platform = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(owner).Options))
                await platform.Database.MigrateAsync();
            IModule[] baselineModules = [new ProjectsModule(), new DocumentsModule()];
            ModuleCatalog baselineCatalog = new(baselineModules);
            ServiceCollection baselineServices = new();
            foreach (IModule module in baselineCatalog.Modules)
                module.Register(baselineServices, configuration);
            using ServiceProvider baselineProvider = baselineServices.BuildServiceProvider();
            IApplicationModelContributor[] baselineContributors = baselineProvider
                .GetServices<IApplicationModelContributor>().ToArray();
            await using (ApplicationDbContext application = new(
                new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(owner).Options,
                baselineContributors,
                moduleCatalog: baselineCatalog))
                await application.Database.MigrateAsync();

            await ApplyModuleMigrationsAsync(owner, baselineCatalog.Modules);
            await CreateProbeTableAsync(owner);
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
                ["Email:From"] = "work-test@example.test",
                ["Email:Security"] = "StartTls"
            };
            IConfiguration runtimeConfiguration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            ServiceCollection services = new();
            services.AddSingleton(runtimeConfiguration);
            services.AddLogging();
            services.AddApplication();
            services.AddModules(runtimeConfiguration, modules);
            services.AddInfrastructure(runtimeConfiguration);
            services.AddSingleton(new ProbeExecutionBehavior());
            ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            return new(postgres, owner, provider);
        }

        public async Task<SeededWork> SeedUserWorkAsync(
            string operationKey,
            bool failAfterWrites = false,
            bool cancel = false,
            bool holdHandler = false)
        {
            SeededOrganization seeded = await SeedOrganizationAsync(["probe.write"]);
            Guid recordId = await SeedRecordAsync(seeded.OrganizationId, seeded.ActorId, canDisclose: true);
            return await SeedWorkAsync(seeded, "probe", operationKey, ModuleExecutionKind.UserCommand,
                recordId, seeded.ActorId, null, null, null, failAfterWrites, cancel, holdHandler);
        }

        public async Task AssertBaselineModelStableAsync()
        {
            IModule[] baselineModules = [new ProjectsModule(), new DocumentsModule()];
            ModuleCatalog baselineCatalog = new(baselineModules);
            ServiceCollection baselineServices = new();
            IConfiguration configuration = new ConfigurationBuilder().Build();
            foreach (IModule module in baselineCatalog.Modules)
                module.Register(baselineServices, configuration);
            using ServiceProvider baselineProvider = baselineServices.BuildServiceProvider();
            IApplicationModelContributor[] baselineContributors = baselineProvider
                .GetServices<IApplicationModelContributor>().ToArray();
            await using ApplicationDbContext context = new(
                new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(owner).Options,
                baselineContributors,
                moduleCatalog: baselineCatalog);
            context.Database.HasPendingModelChanges().ShouldBeFalse();
        }

        public async Task<SeededWork> SeedCommittedEventWorkAsync(bool canDisclose = true)
        {
            SeededOrganization seeded = await SeedOrganizationAsync(["probe.write"]);
            Guid recordId = await SeedRecordAsync(seeded.OrganizationId, seeded.ActorId, canDisclose);
            Guid sourceId = Guid.CreateVersion7();
            ProbePayload eventPayload = new(recordId, seeded.ActorId, false, false, false, "private-payload-marker", null);
            string sourcePayload = JsonSerializer.Serialize(eventPayload, WebJson);
            string catalogVersion = OutboxCatalogVersion.Compute(services.GetRequiredService<ModuleCatalog>());
            OutboxContext outboxContext = new(seeded.OrganizationId, seeded.ActorId, seeded.ActorId, seeded.MembershipId,
                nameof(ModuleExecutionKind.UserCommand), "probe.command", 1, "probe.committed", 1, catalogVersion, null);
            await using (ApplicationDbContext context = new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(owner).Options))
            {
                context.OutboxMessages.Add(new OutboxMessage
                {
                    Id = sourceId,
                    Type = "probe.committed",
                    Payload = sourcePayload,
                    ContextVersion = 1,
                    ContextJson = JsonSerializer.Serialize(outboxContext, WebJson),
                    PayloadHash = OutboxPayloadHash.Compute(sourcePayload)
                });
                await context.SaveChangesAsync();
            }
            return await SeedWorkAsync(seeded, "probe", "probe.effect", ModuleExecutionKind.CommittedEvent,
                recordId, ProbeModule.AuditActorId, "probe.effects", 1, sourceId, payloadOverride: eventPayload);
        }

        public async Task<SeededWork> SeedScheduledWorkAsync(string grantKey = "probe.scheduled-grant")
        {
            SeededOrganization seeded = await SeedOrganizationAsync([]);
            Guid recordId = await SeedRecordAsync(seeded.OrganizationId, seeded.ActorId, canDisclose: true);
            return await SeedWorkAsync(seeded, "probe.scheduled", "probe.scheduled", ModuleExecutionKind.ScheduledSystem,
                recordId, ProbeModule.AuditActorId, grantKey, 1, null);
        }

        public async Task<ModuleExecutionOutcome> ExecuteAsync(SeededWork work, string? expectedOperation = null)
        {
            await using AsyncServiceScope scope = services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IOrganizationWorkExecutor>().ExecuteAsync(
                new ModuleExecutionRequest(work.WorkId, expectedOperation ?? work.OperationKey, 1), CancellationToken.None);
        }

        public IModuleWorkStore Store => services.GetRequiredService<IModuleWorkStore>();
        public Task<ModuleWorkClaim> ClaimAsync(SeededWork work) => Store.ClaimAsync(
            new(work.WorkId, work.OperationKey, 1), CancellationToken.None);
        public Task ExpireLeaseAsync(SeededWork work) => ExecuteOwnerAsync(
            "UPDATE infrastructure.module_work_items SET \"LeaseUntilUtc\"=CURRENT_TIMESTAMP - interval '1 second' WHERE \"Id\"=@work",
            ("work", work.WorkId));

        public async Task<ModuleExecutionOutcome> ExecuteWithoutProbeModuleAsync(SeededWork work)
        {
            IConfiguration configuration = services.GetRequiredService<IConfiguration>();
            ServiceCollection isolated = new();
            isolated.AddSingleton(configuration);
            isolated.AddLogging();
            isolated.AddApplication();
            isolated.AddModules(configuration, [new ProjectsModule(), new DocumentsModule()]);
            isolated.AddInfrastructure(configuration);
            await using ServiceProvider provider = isolated.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            return await provider.GetRequiredService<IOrganizationWorkExecutor>().ExecuteAsync(
                new(work.WorkId, work.OperationKey, 1), CancellationToken.None);
        }

        public async Task ExecuteCancelledAsync(SeededWork work)
        {
            using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(2));
            await using AsyncServiceScope scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IOrganizationWorkExecutor>().ExecuteAsync(
                new ModuleExecutionRequest(work.WorkId, work.OperationKey, 1), cancellation.Token);
        }

        public Task HandlerReachedAsync() => behavior.WaitUntilReachedAsync();
        public void ReleaseHandler() => behavior.Release();

        public async Task<int> RevokeMembershipAsync(Guid organizationId, Guid actorId)
        {
            await using NpgsqlConnection connection = new(owner);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("DELETE FROM platform.memberships WHERE \"OrganizationId\"=@organization AND \"UserId\"=@actor", connection);
            command.Parameters.AddWithValue("organization", organizationId);
            command.Parameters.AddWithValue("actor", actorId);
            return await command.ExecuteNonQueryAsync();
        }

        public async Task<Guid> CreateOrganizationAsync() => (await SeedOrganizationAsync([])).OrganizationId;

        public async Task<Guid> SeedRecordAsync(Guid organizationId, Guid recipientId, bool canDisclose, string marker = "seed")
        {
            Guid id = Guid.CreateVersion7();
            await using NpgsqlConnection connection = new(owner);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("""
                INSERT INTO app.probe_records ("Id","OrganizationId","RecipientActorId","CanDisclose","Marker","EffectCount")
                VALUES (@id,@organization,@recipient,@disclose,@marker,0)
                """, connection);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("organization", organizationId);
            command.Parameters.AddWithValue("recipient", recipientId);
            command.Parameters.AddWithValue("disclose", canDisclose);
            command.Parameters.AddWithValue("marker", marker);
            await command.ExecuteNonQueryAsync();
            return id;
        }

        public async Task SetRecordDisclosureAsync(Guid organizationId, Guid recordId, bool allowed) =>
            await ExecuteOwnerAsync("UPDATE app.probe_records SET \"CanDisclose\"=@allowed WHERE \"OrganizationId\"=@organization AND \"Id\"=@id",
                ("allowed", allowed), ("organization", organizationId), ("id", recordId));

        public Task SetTargetRecordAsync(SeededWork work, Guid targetRecordId) => ExecuteOwnerAsync(
            "UPDATE infrastructure.module_work_items SET \"Payload\"=CAST(@payload AS jsonb), \"PayloadHash\"=@hash WHERE \"Id\"=@work",
            ("payload", ChangedPayload(work, targetRecordId)),
            ("hash", OutboxPayloadHash.Compute(ChangedPayload(work, targetRecordId))),
            ("work", work.WorkId));

        public Task SetAttemptsAsync(SeededWork work, int attempts) => ExecuteOwnerAsync(
            "UPDATE infrastructure.module_work_items SET \"Attempts\"=@attempts WHERE \"Id\"=@work",
            ("attempts", attempts), ("work", work.WorkId));

        public Task SetOrganizationActiveAsync(SeededWork work, bool active) => ExecuteOwnerAsync(
            "UPDATE platform.organizations SET \"IsActive\"=@active WHERE \"Id\"=@organization",
            ("active", active), ("organization", work.OrganizationId));

        public Task SimulateUncertainAcknowledgementAsync(SeededWork work) => ExecuteOwnerAsync(
            "UPDATE infrastructure.module_work_items SET \"State\"='queued', \"LastFailureCode\"='acknowledgement_lost' WHERE \"Id\"=@work",
            ("work", work.WorkId));

        private string ChangedPayload(SeededWork work, Guid targetRecordId)
        {
            using NpgsqlConnection connection = new(owner);
            connection.Open();
            using NpgsqlCommand command = new("SELECT \"Payload\"::text FROM infrastructure.module_work_items WHERE \"Id\"=@work", connection);
            command.Parameters.AddWithValue("work", work.WorkId);
            ProbePayload current = JsonSerializer.Deserialize<ProbePayload>((string)command.ExecuteScalar()!, WebJson)!;
            return JsonSerializer.Serialize(current with { TargetRecordId = targetRecordId }, WebJson);
        }

        public async Task<string> ReadMarkerAsync(Guid organizationId, Guid recordId)
        {
            await using NpgsqlConnection connection = new(owner);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("SELECT \"Marker\" FROM app.probe_records WHERE \"OrganizationId\"=@organization AND \"Id\"=@id", connection);
            command.Parameters.AddWithValue("organization", organizationId);
            command.Parameters.AddWithValue("id", recordId);
            return (string)(await command.ExecuteScalarAsync())!;
        }

        public async Task<long> CountEffectsAsync(Guid organizationId) => await CountOwnerAsync(
            "SELECT COALESCE(sum(\"EffectCount\"),0)::bigint FROM app.probe_records WHERE \"OrganizationId\"=@organization", ("organization", organizationId));

        public async Task<long> CountAuditIntentsAsync(Guid organizationId) => await CountOwnerAsync(
            "SELECT count(*) FROM platform.audit_intents WHERE \"OrganizationId\"=@organization", ("organization", organizationId));

        public async Task<string> ReadAuditIntentsAsync(Guid organizationId) => await ReadJsonOwnerAsync(
            "SELECT COALESCE(jsonb_agg(jsonb_build_object('operation',\"Operation\",'details',\"Details\")), '[]'::jsonb)::text FROM platform.audit_intents WHERE \"OrganizationId\"=@organization",
            ("organization", organizationId));

        public async Task<string> ReadCompletionAndAuditAsync(SeededWork work) => await ReadJsonOwnerAsync("""
            SELECT jsonb_build_object(
                'completion', (SELECT row_to_json(c) FROM app.module_work_completions c WHERE c."OrganizationId"=@organization AND c."WorkId"=@work),
                'audit', (SELECT jsonb_agg("Details") FROM platform.audit_intents WHERE "OrganizationId"=@organization))::text
            """, ("organization", work.OrganizationId), ("work", work.WorkId));

        public async Task<long> ReadCompletionCountAsync(Guid organizationId, Guid workId) => await CountOwnerAsync(
            "SELECT count(*) FROM app.module_work_completions WHERE \"OrganizationId\"=@organization AND \"WorkId\"=@work",
            ("organization", organizationId), ("work", workId));

        public async Task<long> ReadOutboxCountAsync(Guid organizationId) => await CountOwnerAsync(
            "SELECT count(*) FROM platform.outbox_messages WHERE \"ContextJson\"->>'organizationId'=@organization", ("organization", organizationId.ToString()));

        public async Task<long> CountAsOrganizationRuntimeWithoutContextAsync()
        {
            await using NpgsqlConnection connection = new(organizationConnection);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("SELECT count(*) FROM app.probe_records", connection);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        public async Task TouchOrganizationScopeOnPooledConnectionAsync(Guid organizationId, Guid actorId)
        {
            await using NpgsqlConnection connection = new(organizationConnection);
            await connection.OpenAsync();
            await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync())
            {
                await using NpgsqlCommand command = new("SELECT set_config('app.organization_id',@organization,true),set_config('app.actor_id',@actor,true)", connection, transaction);
                command.Parameters.AddWithValue("organization", organizationId.ToString());
                command.Parameters.AddWithValue("actor", actorId.ToString());
                await command.ExecuteNonQueryAsync();
                ((long)(await new NpgsqlCommand("SELECT count(*) FROM app.probe_records", connection, transaction).ExecuteScalarAsync())!)
                    .ShouldBeGreaterThan(0);
                await transaction.CommitAsync();
            }
            await using NpgsqlTransaction nextTransaction = await connection.BeginTransactionAsync();
            ((long)(await new NpgsqlCommand("SELECT count(*) FROM app.probe_records", connection, nextTransaction).ExecuteScalarAsync())!)
                .ShouldBe(0);
            await nextTransaction.CommitAsync();
        }

        private async Task<SeededOrganization> SeedOrganizationAsync(string[] permissions)
        {
            Organization organization = Organization.Create("Work execution", $"work-{Guid.NewGuid():N}"[..30]);
            Guid actorId = Guid.CreateVersion7();
            Role role = Role.Create(organization.Id, "Work actor", isSystem: true);
            role.SetPermissions(permissions);
            Membership membership = Membership.Create(organization.Id, actorId);
            membership.AssignRole(role.Id);
            OrganizationDataPlacementRecord placement = OrganizationDataPlacementRecord.Begin(
                organization.Id, OrganizationDataPlacementKind.Shared, "postgres", null);
            placement.MarkReady(new NpgsqlConnectionStringBuilder(owner).Database!, "integration-test", "baseline", DateTimeOffset.UtcNow);
            await using PlatformDbContext context = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(owner).Options);
            context.AddRange(organization, role, membership, placement);
            await context.SaveChangesAsync();
            return new(organization.Id, actorId, membership.Id);
        }

        private async Task<SeededWork> SeedWorkAsync(
            SeededOrganization seeded,
            string jobKey,
            string operationKey,
            ModuleExecutionKind kind,
            Guid recordId,
            Guid effectiveActor,
            string? grantKey,
            int? grantVersion,
            Guid? sourceMessageId,
            bool failAfterWrites = false,
            bool cancel = false,
            bool holdHandler = false,
            ProbePayload? payloadOverride = null)
        {
            Guid workId = Guid.CreateVersion7();
            ProbePayload workPayload = payloadOverride ?? new(recordId, seeded.ActorId, failAfterWrites, cancel, holdHandler, "private-payload-marker", null);
            string payload = JsonSerializer.Serialize(workPayload, WebJson);
            ModuleExecutionIdentity identity = new(seeded.OrganizationId, effectiveActor, seeded.ActorId,
                kind == ModuleExecutionKind.UserCommand ? seeded.MembershipId : null,
                kind, grantKey, grantVersion, sourceMessageId, 1, null);
            string context = JsonSerializer.Serialize(new ModuleWorkEnvelope(1, "probe", identity), WebJson);
            await using ModuleWorkDbContext db = new(new DbContextOptionsBuilder<ModuleWorkDbContext>().UseNpgsql(owner).Options);
            db.ModuleWorkItems.Add(new ModuleWorkItem
            {
                Id = workId,
                OrganizationId = seeded.OrganizationId,
                ActorId = effectiveActor,
                RequestId = Guid.CreateVersion7(),
                ExecutionIdentityKey = kind == ModuleExecutionKind.UserCommand ? $"user.{seeded.ActorId:N}" : $"system.{grantKey}",
                JobKey = jobKey,
                JobVersion = 1,
                OperationKey = operationKey,
                OperationVersion = 1,
                ContextVersion = 1,
                ExecutionContextJson = context,
                Payload = payload,
                PayloadHash = OutboxPayloadHash.Compute(payload),
                IntentHash = new string('a', 64),
                SubmissionMode = "enqueue",
                State = "queued",
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            return new(workId, seeded.OrganizationId, seeded.ActorId, effectiveActor, operationKey, recordId);
        }

        private async Task<long> CountOwnerAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using NpgsqlConnection connection = new(owner);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new(sql, connection);
            foreach ((string name, object value) in parameters) command.Parameters.AddWithValue(name, value);
            return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }

        private async Task<string> ReadJsonOwnerAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using NpgsqlConnection connection = new(owner);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new(sql, connection);
            foreach ((string name, object value) in parameters) command.Parameters.AddWithValue(name, value);
            return (string)(await command.ExecuteScalarAsync())!;
        }

        private async Task ExecuteOwnerAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using NpgsqlConnection connection = new(owner);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new(sql, connection);
            foreach ((string name, object value) in parameters) command.Parameters.AddWithValue(name, value);
            await command.ExecuteNonQueryAsync();
        }

        private static async Task CreateProbeTableAsync(string owner)
        {
            await using NpgsqlConnection connection = new(owner);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("""
                CREATE TABLE app.probe_records (
                    "Id" uuid PRIMARY KEY,
                    "OrganizationId" uuid NOT NULL,
                    "RecipientActorId" uuid NOT NULL,
                    "CanDisclose" boolean NOT NULL,
                    "Marker" text NOT NULL,
                    "EffectCount" integer NOT NULL DEFAULT 0);
                CREATE INDEX probe_records_organization_id ON app.probe_records ("OrganizationId", "Id");
                ALTER TABLE app.probe_records ENABLE ROW LEVEL SECURITY;
                ALTER TABLE app.probe_records FORCE ROW LEVEL SECURITY;
                CREATE POLICY probe_records_organization ON app.probe_records
                    USING ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid)
                    WITH CHECK ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid);
                """, connection);
            await command.ExecuteNonQueryAsync();
        }

        private static async Task ApplyModuleMigrationsAsync(string owner, IReadOnlyList<IModule> modules)
        {
            IReadOnlyList<PendingModuleMigration> migrations = ModuleMigrationPlan.Build(modules, []);
            await using NpgsqlConnection connection = new(owner);
            await connection.OpenAsync();
            foreach (PendingModuleMigration migration in migrations)
            {
                await using NpgsqlCommand command = new(migration.Sql, connection);
                await command.ExecuteNonQueryAsync();
            }
        }

        private static RuntimeDatabaseRoles RuntimeRoles() => new(
            PostgresRuntimeRoleFixture.OrganizationRole,
            PostgresRuntimeRoleFixture.PlatformRole,
            PostgresRuntimeRoleFixture.IdentityRole,
            PostgresRuntimeRoleFixture.OutboxRole,
            PostgresRuntimeRoleFixture.WorkCoordinatorRole);

        private static string RoleConnection(string owner, string role, string password) =>
            new NpgsqlConnectionStringBuilder(owner) { Username = role, Password = password }.ConnectionString;

        public async ValueTask DisposeAsync()
        {
            await services.DisposeAsync();
            await postgres.DisposeAsync();
        }

        private sealed record SeededOrganization(Guid OrganizationId, Guid ActorId, Guid MembershipId);
    }

    private sealed record SeededWork(
        Guid WorkId,
        Guid OrganizationId,
        Guid ActorId,
        Guid EffectiveActorId,
        string OperationKey,
        Guid PayloadRecordId)
    {
        public Guid OriginActorId => ActorId;
    }

    private sealed record ProbePayload(
        Guid RecordId,
        Guid RecipientActorId,
        bool FailAfterWrites,
        bool Cancel,
        bool HoldHandler,
        string Marker,
        Guid? TargetRecordId);

    private sealed class ProbeRecord : IOrganizationOwned
    {
        public Guid Id { get; set; }
        public Guid OrganizationId { get; set; }
        public Guid RecipientActorId { get; set; }
        public bool CanDisclose { get; set; }
        public string Marker { get; set; } = string.Empty;
        public int EffectCount { get; set; }
    }

    private sealed class ProbeModelContributor : IApplicationModelContributor
    {
        public string ModuleId => "probe";
        public void Configure(ModelBuilder modelBuilder) => modelBuilder.Entity<ProbeRecord>(entity =>
        {
            entity.ToTable("probe_records", "app");
            entity.HasKey(record => record.Id);
            entity.HasIndex(record => new { record.OrganizationId, record.Id });
            entity.Property(record => record.Marker).HasMaxLength(200);
        });
    }

    private sealed class ProbeModule : IModule
    {
        public static readonly Guid AuditActorId = Guid.Parse("e5edb541-d022-41ea-ab22-f817bbdf38c3");
        private const string Grant = "probe.effects";
        private const string Resource = "probe.records";
        private static readonly string[] NoPermissions = [];
        public ModuleDescriptor Descriptor { get; } = new(
            "probe", "Execution probe", "1.0.0", "Integration test execution probe.", [], [],
            ModuleCapabilities.Api | ModuleCapabilities.BackgroundWork | ModuleCapabilities.Data, [])
        {
            DefaultDataOwnership = ModuleDataOwnership.Organization,
            Permissions = [new PermissionDescriptor("probe.write", "Probe write", "Write probe records")],
            DataResources = [new DataResourceDescriptor(Resource, "app", "probe_records", ModuleDataOwnership.Organization,
                typeof(ProbeRecord).FullName, "probe_records_organization")],
            Operations =
            [
                new("probe", "probe.command", 1, ModuleExecutionKind.UserCommand, "probe.command.payload", 1,
                    ["probe.write"], Resource, null, 4096),
                new("probe", "probe.effect", 1, ModuleExecutionKind.CommittedEvent, "probe.effect.payload", 1,
                    NoPermissions, Resource, Grant, 4096),
                new("probe", "probe.scheduled", 1, ModuleExecutionKind.ScheduledSystem, "probe.scheduled.payload", 1,
                    NoPermissions, Resource, "probe.scheduled-grant", 4096)
            ],
            SystemGrants =
            [
                new(Grant, 1, "probe", AuditActorId, ["probe.effect"], [Resource]),
                new("probe.scheduled-grant", 1, "probe", AuditActorId, ["probe.scheduled"], [Resource])
            ],
            IntegrationEvents =
            [
                new("probe.committed", 1, "probe", typeof(JsonElement), element => element, 4096),
                new("probe.followup", 1, "probe", typeof(ProbeFollowup), element => element.Deserialize<ProbeFollowup>(PayloadJson)!, 4096)
            ],
            EventConsumers =
            [
                new("probe.effect-consumer", "probe", [new ModuleEventVersion("probe.committed", 1)], "probe.effect", 1, Grant)
            ],
            Jobs =
            [
                new("probe", "probe", 1, "probe.command", 1, ModuleExecutionKind.UserCommand, "probe.command.payload", 1, 4096),
                new("probe", "probe.scheduled", 1, "probe.scheduled", 1, ModuleExecutionKind.ScheduledSystem, "probe.scheduled.payload", 1, 4096)
            ]
        };

        public void Register(IServiceCollection services, IConfiguration configuration)
        {
            services.AddScoped<IApplicationModelContributor, ProbeModelContributor>();
            services.AddScoped<IModuleOperationHandler>(provider => new ProbeOperationHandler("probe.command",
                provider.GetRequiredService<ApplicationDbContext>(), provider.GetRequiredService<ProbeExecutionBehavior>(), provider.GetRequiredService<IOutboxWriter>()));
            services.AddScoped<IModuleOperationHandler>(provider => new ProbeOperationHandler("probe.effect",
                provider.GetRequiredService<ApplicationDbContext>(), provider.GetRequiredService<ProbeExecutionBehavior>(), provider.GetRequiredService<IOutboxWriter>()));
            services.AddScoped<IModuleOperationHandler>(provider => new ProbeOperationHandler("probe.scheduled",
                provider.GetRequiredService<ApplicationDbContext>(), provider.GetRequiredService<ProbeExecutionBehavior>(), provider.GetRequiredService<IOutboxWriter>()));
            services.AddScoped<ProbeOperationHandler>(provider => new ProbeOperationHandler("probe.effect",
                provider.GetRequiredService<ApplicationDbContext>(), provider.GetRequiredService<ProbeExecutionBehavior>(), provider.GetRequiredService<IOutboxWriter>()));
            services.AddScoped<ModuleEventConsumerBinding>(provider => new("probe", "probe.effect-consumer",
                provider.GetRequiredService<ProbeOperationHandler>()));
            services.AddScoped<IModuleOperationResourcePolicy, ProbeResourcePolicy>();
        }
    }

    private sealed class ProbeExecutionBehavior
    {
        private TaskCompletionSource reached = NewSignal();
        private TaskCompletionSource release = NewSignal();
        public Task ReachAsync() { reached.TrySetResult(); return release.Task; }
        public Task WaitUntilReachedAsync() => reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        public void Release() => release.TrySetResult();
        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ProbeOperationHandler(
        string operationKey,
        ApplicationDbContext db,
        ProbeExecutionBehavior behavior,
        IOutboxWriter outbox) : IModuleOperationHandler, IModuleEventConsumer
    {
        public string ModuleId => "probe";
        public string OperationKey { get; } = operationKey;
        public int OperationVersion => 1;

        public Task HandleAsync(ModuleOperationContext context, JsonElement payload, CancellationToken cancellationToken)
        {
            return HandleCoreAsync(context, payload, cancellationToken);
        }

        public Task HandleAsync(ModuleEventContext context, JsonElement payload, CancellationToken cancellationToken) =>
            HandleCoreAsync(new ModuleOperationContext(context.MessageId, context.OrganizationId, context.EffectiveActorId,
                context.OriginActorId, ModuleExecutionKind.CommittedEvent, OperationKey, OperationVersion), payload, cancellationToken);

        private async Task HandleCoreAsync(ModuleOperationContext context, JsonElement payload, CancellationToken cancellationToken)
        {
            ProbePayload data = payload.Deserialize<ProbePayload>(PayloadJson)!;
            if (data.HoldHandler)
                await behavior.ReachAsync();
            Guid targetRecordId = data.TargetRecordId ?? data.RecordId;
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE app.probe_records SET \"EffectCount\"=\"EffectCount\"+1, \"Marker\"={context.OperationKey} WHERE \"Id\"={targetRecordId}", cancellationToken);
            outbox.Enqueue(new ProbeFollowup(data.RecordId, context.OperationKey), context.OperationKey, context.OperationVersion);
            await db.SaveChangesAsync(cancellationToken);
            if (data.Cancel)
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            if (data.FailAfterWrites)
                throw new InvalidOperationException("test-handler-failure");
        }
    }

    private sealed record ProbeFollowup(Guid RecordId, string Operation);

    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    private sealed class ProbeResourcePolicy(ApplicationDbContext db) : IModuleOperationResourcePolicy
    {
        public string ModuleId => "probe";
        public string PolicyKey => "probe.records";

        public async Task<bool> AuthorizeAsync(ModuleOperationContext context, JsonElement payload, CancellationToken cancellationToken)
        {
            ProbePayload? data = payload.Deserialize<ProbePayload>(PayloadJson);
            if (data is null)
                return false;
            ProbeRecord? record = await db.Set<ProbeRecord>().AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == data.RecordId, cancellationToken);
            return record is not null && record.CanDisclose && record.RecipientActorId == data.RecipientActorId;
        }
    }
}
