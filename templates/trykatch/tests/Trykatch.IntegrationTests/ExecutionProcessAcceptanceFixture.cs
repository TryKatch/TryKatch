using System.Diagnostics;
using System.Globalization;
using System.Reflection;
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
using Trykatch.Application.Organizations;
using Trykatch.Application.Outbox;
using Trykatch.Domain.Organizations;
using Trykatch.ExecutionAcceptance;
using Trykatch.Identity;
using Trykatch.Infrastructure;
using Trykatch.Infrastructure.Execution;
using Trykatch.Infrastructure.Jobs;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;
using Trykatch.Modules.Documents.Infrastructure;
using Trykatch.Modules.Projects.Application;
using Trykatch.Modules.Projects.Infrastructure;

namespace Trykatch.IntegrationTests;

internal sealed class ExecutionProcessAcceptanceFixture : IAsyncDisposable
{
    internal const string ProjectsRealtimeConsumerKey = "projects.created-realtime";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly string[] RuntimeRoles =
    [
        PostgresRuntimeRoleFixture.OrganizationRole,
        PostgresRuntimeRoleFixture.PlatformRole,
        PostgresRuntimeRoleFixture.IdentityRole,
        PostgresRuntimeRoleFixture.OutboxRole,
        PostgresRuntimeRoleFixture.WorkCoordinatorRole
    ];

    private readonly PostgreSqlContainer postgres;
    private readonly string owner;
    private readonly string organizationRuntime;
    private readonly IReadOnlyDictionary<string, string> runtimeConnections;
    private readonly ServiceProvider parentServices;
    private readonly List<WorkerProcess> workers = [];

    private ExecutionProcessAcceptanceFixture(
        PostgreSqlContainer postgres,
        string owner,
        string organizationRuntime,
        IReadOnlyDictionary<string, string> runtimeConnections,
        ServiceProvider parentServices)
    {
        this.postgres = postgres;
        this.owner = owner;
        this.organizationRuntime = organizationRuntime;
        this.runtimeConnections = runtimeConnections;
        this.parentServices = parentServices;
    }

    public static async Task<ExecutionProcessAcceptanceFixture> StartAsync()
    {
        PostgreSqlContainer postgres = new PostgreSqlBuilder(
            "postgres:18.6-alpine@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f").Build();
        await postgres.StartAsync();
        string owner = postgres.GetConnectionString();
        try
        {
            await PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(owner);
            IModule[] businessModules = [new ProjectsModule(), new DocumentsModule()];
            IModule[] modules = [.. businessModules, new AcceptanceModule()];
            IConfiguration empty = new ConfigurationBuilder().Build();

            await using (IdentityDbContext identity = new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(owner).Options))
                await identity.Database.MigrateAsync();
            await using (PlatformDbContext platform = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(owner).Options))
                await platform.Database.MigrateAsync();

            ModuleCatalog businessCatalog = new(businessModules);
            ServiceCollection baselineModuleServices = new();
            foreach (IModule module in businessCatalog.Modules) module.Register(baselineModuleServices, empty);
            using ServiceProvider baselineModuleProvider = baselineModuleServices.BuildServiceProvider();
            IApplicationModelContributor[] baselineContributors = baselineModuleProvider
                .GetServices<IApplicationModelContributor>().ToArray();
            await using (ApplicationDbContext application = new(
                new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(owner).Options,
                baselineContributors,
                moduleCatalog: businessCatalog))
                await application.Database.MigrateAsync();

            await ApplyModuleMigrationsAsync(owner, businessCatalog.Modules);
            await CreateAcceptanceEffectRelationAsync(owner);

            ModuleCatalog installed = new(modules);
            await InstalledSchemaCatalog.SynchronizeAsync(owner, installed.Descriptors.SelectMany(module =>
                module.DataResources.Select(resource => new InstalledDataResource(module.Id, resource))));
            await RuntimeRoleProvisioner.ProvisionAsync(owner, ToRuntimeRoles());

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
                ["Jobs:Enabled"] = "true",
                ["Email:Host"] = "127.0.0.1",
                ["Email:Port"] = "1",
                ["Email:From"] = "execution-acceptance@example.test",
                ["Email:Security"] = "StartTls"
            };
            IConfiguration runtimeConfiguration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            ServiceCollection services = new();
            services.AddSingleton(runtimeConfiguration);
            services.AddLogging();
            services.AddApplication();
            services.AddModules(runtimeConfiguration, modules);
            services.AddInfrastructure(runtimeConfiguration);
            ServiceProvider parentServices = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            return new(postgres, owner, organization, new Dictionary<string, string>
            {
                [RuntimeDatabaseConnectionContract.Organization] = organization,
                [RuntimeDatabaseConnectionContract.Platform] = platformRuntime,
                [RuntimeDatabaseConnectionContract.Identity] = identityRuntime,
                [RuntimeDatabaseConnectionContract.Outbox] = outbox,
                [RuntimeDatabaseConnectionContract.WorkCoordinator] = coordinator
            }, parentServices);
        }
        catch
        {
            await postgres.DisposeAsync();
            throw;
        }
    }

    public async Task<AcceptedProjectsWork> SeedProjectsCreateWorkAsync()
    {
        Organization organization = Organization.Create("Execution acceptance", $"acceptance-{Guid.NewGuid():N}"[..30]);
        Guid actorId = Guid.CreateVersion7();
        Role role = Role.Create(organization.Id, "Acceptance actor", isSystem: true);
        role.SetPermissions(["projects.manage"]);
        Membership membership = Membership.Create(organization.Id, actorId);
        membership.AssignRole(role.Id);
        OrganizationDataPlacementRecord placement = OrganizationDataPlacementRecord.Begin(
            organization.Id, OrganizationDataPlacementKind.Shared, "postgres", null);
        placement.MarkReady(new NpgsqlConnectionStringBuilder(owner).Database!, "integration-test", "baseline", DateTimeOffset.UtcNow);
        await using (PlatformDbContext platform = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(owner).Options))
        {
            platform.AddRange(organization, role, membership, placement);
            await platform.SaveChangesAsync();
        }

        CreateProjectCommand command = new("Process accepted project", "Seeded through the real Projects create operation.");
        string payload = JsonSerializer.Serialize(command, WebJson);
        Guid workId = Guid.CreateVersion7();
        ModuleExecutionIdentity identity = new(organization.Id, actorId, actorId, membership.Id,
            ModuleExecutionKind.UserCommand, null, null, null, ModuleOperationRegistry.CatalogVersion, null);
        ModuleWorkItem work = new()
        {
            Id = workId,
            OrganizationId = organization.Id,
            ActorId = actorId,
            RequestId = Guid.CreateVersion7(),
            ExecutionIdentityKey = ModuleJobIntent.RequestIdentity(actorId),
            JobKey = "projects.create",
            JobVersion = 1,
            OperationKey = "projects.create",
            OperationVersion = 1,
            ContextVersion = 1,
            ExecutionContextJson = JsonSerializer.Serialize(new ModuleWorkEnvelope(1, "projects", identity), WebJson),
            Payload = payload,
            PayloadHash = OutboxPayloadHash.Compute(payload),
            IntentHash = ModuleJobIntent.IntentHash(payload, "enqueue", null),
            SubmissionMode = "enqueue",
            State = "queued",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await using ModuleWorkDbContext workDb = new(new DbContextOptionsBuilder<ModuleWorkDbContext>().UseNpgsql(owner).Options);
        workDb.ModuleWorkItems.Add(work);
        await workDb.SaveChangesAsync();
        return new(workId, organization.Id, actorId, membership.Id);
    }

    public async Task<Guid> SeedSyntheticTargetAsync(Guid organizationId, Guid actorId)
    {
        Guid target = Guid.CreateVersion7();
        await using NpgsqlConnection connection = new(owner);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("""
            INSERT INTO app.execution_acceptance_effects ("Id","OrganizationId","Kind","ActorId")
            VALUES (@id,@organization,@kind,@actor)
            """, connection);
        command.Parameters.AddWithValue("id", target);
        command.Parameters.AddWithValue("organization", organizationId);
        command.Parameters.AddWithValue("kind", AcceptanceEffect.SyntheticTarget);
        command.Parameters.AddWithValue("actor", actorId);
        await command.ExecuteNonQueryAsync();
        return target;
    }

    public async Task<ProcessResult> RunProducerAsync(AcceptedProjectsWork work, Guid targetId)
    {
        ProcessStartInfo start = CreateChildStart("producer");
        start.Environment["TRYKATCH_ACCEPTANCE_PROJECT_WORK_ID"] = work.WorkId.ToString();
        start.Environment["TRYKATCH_ACCEPTANCE_ORGANIZATION_ID"] = work.OrganizationId.ToString();
        start.Environment["TRYKATCH_ACCEPTANCE_ACTOR_ID"] = work.ActorId.ToString();
        start.Environment["TRYKATCH_ACCEPTANCE_JOB_TARGET_ID"] = targetId.ToString();
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Producer process did not start.");
        (int exit, string output, string error) = await ProcessExecution.WaitAsync(process, TimeSpan.FromSeconds(30));
        return new(exit, output, error);
    }

    public async Task<WorkerProcess> StartWorkerAsync(string instance)
    {
        string readyPath = Path.Combine(Path.GetTempPath(), $"trykatch-worker-{Guid.NewGuid():N}.json");
        ProcessStartInfo start = CreateChildStart("worker");
        start.Environment["TRYKATCH_ACCEPTANCE_INSTANCE"] = instance;
        start.Environment["TRYKATCH_ACCEPTANCE_READY_FILE"] = readyPath;
        Process process = Process.Start(start) ?? throw new InvalidOperationException("Worker process did not start.");
        WorkerReady ready;
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            while (!File.Exists(readyPath))
            {
                if (process.HasExited)
                    throw new InvalidOperationException($"Worker {instance} exited before readiness with code {process.ExitCode}.");
                await Task.Delay(50, timeout.Token);
            }
            ready = JsonSerializer.Deserialize<WorkerReady>(await File.ReadAllTextAsync(readyPath), WebJson)
                ?? throw new InvalidOperationException($"Worker {instance} wrote invalid readiness metadata.");
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await ProcessExecution.WaitAsync(process, TimeSpan.FromSeconds(10));
            process.Dispose();
            throw;
        }
        finally
        {
            File.Delete(readyPath);
            File.Delete(readyPath + ".tmp");
        }
        WorkerProcess worker = new(process, ready);
        workers.Add(worker);
        return worker;
    }

    public async Task<NpgsqlConnection> HoldEffectTableAsync()
    {
        NpgsqlConnection connection = new(owner);
        try
        {
            await connection.OpenAsync();
            // Parent-only gate: neither effect can commit until both real cluster members exist.
            await using NpgsqlCommand command = new(
                "BEGIN; LOCK TABLE app.execution_acceptance_effects IN ACCESS EXCLUSIVE MODE", connection);
            await command.ExecuteNonQueryAsync();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task WaitForClusterMembersAsync(string first, string second)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        while (await ScalarLongAsync("""
            SELECT count(*) FROM infrastructure.qrtz_scheduler_state
            WHERE sched_name='TrykatchModuleJobs' AND instance_name IN (@first,@second)
            """, ("first", first), ("second", second)) != 2)
            await Task.Delay(50, deadline.Token);
    }

    public async Task<ModuleWorkReceipt> ScheduleSyntheticJobAsync(Guid organizationId, Guid actorId, Guid targetId)
    {
        await using AsyncServiceScope scope = parentServices.CreateAsyncScope();
        await using IOrganizationDataScope transaction = await scope.ServiceProvider
            .GetRequiredService<IOrganizationDataScopeFactory>().BeginAsync(organizationId, actorId, CancellationToken.None);
        OrganizationAccess? access = await scope.ServiceProvider.GetRequiredService<IOrganizationAccessResolver>()
            .ResolveAsync(actorId, organizationId, CancellationToken.None);
        access.ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<IOrganizationContextInitializer>().Initialize(access!);
        ModuleWorkReceipt receipt = await scope.ServiceProvider.GetRequiredService<IModuleJobScheduler>().ScheduleAsync(
            new(Guid.NewGuid(), AcceptanceModule.JobKey, 1, JsonSerializer.SerializeToElement(new AcceptanceJobPayload(targetId))),
            DateTimeOffset.UtcNow.AddHours(1));
        await transaction.CommitAsync(CancellationToken.None);
        return receipt;
    }

    public Task<int> MakeWorkEligibleAsync(Guid workId) => ExecuteOwnerAsync(
        "UPDATE infrastructure.module_work_items SET \"DueAtUtc\"=CURRENT_TIMESTAMP - interval '1 second', \"NextAttemptAtUtc\"=NULL WHERE \"Id\"=@id AND \"State\"='queued'",
        ("id", workId));

    public async Task SimulateLostAcknowledgementsAsync(Guid messageId, Guid workId)
    {
        (await ExecuteOwnerAsync(
            "UPDATE infrastructure.module_event_deliveries SET \"State\"='queued', \"Attempts\"=1, \"NextAttemptAtUtc\"=CURRENT_TIMESTAMP - interval '1 second', \"LeaseId\"=NULL, \"LeaseUntilUtc\"=NULL WHERE \"MessageId\"=@message AND \"ConsumerKey\"=@consumer",
            ("message", messageId), ("consumer", AcceptanceModule.ConsumerKey))).ShouldBe(1);
        (await ExecuteOwnerAsync(
            "UPDATE infrastructure.module_event_deliveries SET \"State\"='queued', \"Attempts\"=1, \"NextAttemptAtUtc\"=CURRENT_TIMESTAMP - interval '1 second', \"LeaseId\"=NULL, \"LeaseUntilUtc\"=NULL WHERE \"MessageId\"=@message AND \"ConsumerKey\"=@consumer",
            ("message", messageId), ("consumer", ProjectsRealtimeConsumerKey))).ShouldBe(1);
        (await ExecuteOwnerAsync(
            "UPDATE infrastructure.module_work_items SET \"State\"='queued', \"LastFailureCode\"='acknowledgement_lost' WHERE \"Id\"=@work",
            ("work", workId))).ShouldBe(1);
    }

    public Task<int> RevokeMembershipAsync(Guid organizationId, Guid actorId) => ExecuteOwnerAsync(
        "DELETE FROM platform.memberships WHERE \"OrganizationId\"=@organization AND \"UserId\"=@actor",
        ("organization", organizationId), ("actor", actorId));

    public Task<long> CountEffectsAsync(Guid organizationId) => ScalarLongAsync(
        "SELECT count(*) FROM app.execution_acceptance_effects WHERE \"OrganizationId\"=@organization AND (\"Kind\"=@eventKind OR \"CompletedAtUtc\" IS NOT NULL)",
        ("organization", organizationId), ("eventKind", AcceptanceEffect.ProjectEvent));

    public Task<long> CountEventInboxAsync(Guid organizationId, Guid messageId) => ScalarLongAsync(
        "SELECT count(*) FROM app.module_event_inbox WHERE \"OrganizationId\"=@organization AND \"MessageId\"=@message AND \"ConsumerKey\"=@consumer",
        ("organization", organizationId), ("message", messageId), ("consumer", AcceptanceModule.ConsumerKey));

    public Task<long> CountEventDeliveriesAsync(Guid messageId) => ScalarLongAsync(
        "SELECT count(*) FROM infrastructure.module_event_deliveries WHERE \"MessageId\"=@message AND \"ConsumerKey\"=@consumer",
        ("message", messageId), ("consumer", AcceptanceModule.ConsumerKey));

    public Task<long> CountProjectsRealtimeInboxAsync(Guid organizationId, Guid messageId) => ScalarLongAsync(
        "SELECT count(*) FROM app.module_event_inbox WHERE \"OrganizationId\"=@organization AND \"MessageId\"=@message AND \"ConsumerKey\"=@consumer",
        ("organization", organizationId), ("message", messageId), ("consumer", ProjectsRealtimeConsumerKey));

    public Task<long> CountProjectsRealtimeDeliveryAsync(Guid messageId) => ScalarLongAsync(
        "SELECT count(*) FROM infrastructure.module_event_deliveries WHERE \"MessageId\"=@message AND \"ConsumerKey\"=@consumer",
        ("message", messageId), ("consumer", ProjectsRealtimeConsumerKey));

    public Task<long> CountProjectsRealtimeHistoryAsync(Guid organizationId, Guid sourceMessageId) => ScalarLongAsync(
        "SELECT count(*) FROM app.realtime_history WHERE \"OrganizationId\"=@organization AND \"SourceMessageId\"=@message",
        ("organization", organizationId), ("message", sourceMessageId));

    public async Task<ProjectRealtimeEvidence> ReadProjectsRealtimeHistoryAsync(Guid organizationId, Guid sourceMessageId)
    {
        await using NpgsqlConnection connection = new(owner);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("""
            SELECT "EventId"::text, "SourceMessageId"::text, "ModuleId", "EventKey", "SchemaVersion",
                   "PolicyOwnerModuleId", "PolicyKey", "PolicyVersion", "AudienceKind", "UserId"::text,
                   "ResourceKey", "ResourceId"::text
            FROM app.realtime_history
            WHERE "OrganizationId"=@organization AND "SourceMessageId"=@message
            """, connection);
        command.Parameters.AddWithValue("organization", organizationId);
        command.Parameters.AddWithValue("message", sourceMessageId);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue("the Projects consumer should append its realtime history row");
        ProjectRealtimeEvidence evidence = new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
            reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetString(5), reader.GetString(6),
            reader.GetInt32(7), reader.GetInt32(8), reader.IsDBNull(9) ? null : Guid.Parse(reader.GetString(9)),
            reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : Guid.Parse(reader.GetString(11)));
        (await reader.ReadAsync()).ShouldBeFalse("one source event should produce one Projects history row");
        return evidence;
    }

    public async Task CreateProjectsRealtimeInboxFailureGateAsync()
    {
        await ExecuteOwnerAsync($"""
            CREATE FUNCTION app.fail_projects_realtime_inbox_insert() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."ConsumerKey" = '{ProjectsRealtimeConsumerKey}' THEN
                    RAISE EXCEPTION 'injected Projects realtime inbox failure';
                END IF;
                RETURN NEW;
            END;
            $$;
            CREATE TRIGGER fail_projects_realtime_inbox_insert
                BEFORE INSERT ON app.module_event_inbox
                FOR EACH ROW EXECUTE FUNCTION app.fail_projects_realtime_inbox_insert();
            """);
    }

    public Task DropProjectsRealtimeInboxFailureGateAsync() => ExecuteOwnerAsync("""
        DROP TRIGGER fail_projects_realtime_inbox_insert ON app.module_event_inbox;
        DROP FUNCTION app.fail_projects_realtime_inbox_insert();
        """);

    public Task<int> MakeProjectsRealtimeDeliveryEligibleAsync(Guid messageId) => ExecuteOwnerAsync(
        "UPDATE infrastructure.module_event_deliveries SET \"State\"='queued', \"NextAttemptAtUtc\"=CURRENT_TIMESTAMP - interval '1 second', \"LeaseId\"=NULL, \"LeaseUntilUtc\"=NULL WHERE \"MessageId\"=@message AND \"ConsumerKey\"=@consumer",
        ("message", messageId), ("consumer", ProjectsRealtimeConsumerKey));

    public async Task WaitForProjectsRealtimeDeliveryStateAsync(Guid messageId, string state, bool attempted = false)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(40));
        string attemptPredicate = attempted ? " AND \"Attempts\">0" : string.Empty;
        while (await ScalarLongAsync($"SELECT count(*) FROM infrastructure.module_event_deliveries WHERE \"MessageId\"=@message AND \"ConsumerKey\"=@consumer AND \"State\"=@state{attemptPredicate}",
                   ("message", messageId), ("consumer", ProjectsRealtimeConsumerKey), ("state", state)) != 1)
            await Task.Delay(50, deadline.Token);
    }

    public async Task WaitForProjectsRealtimeCompletionAsync(Guid organizationId, Guid messageId)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(45));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (await CountProjectsRealtimeInboxAsync(organizationId, messageId) == 1
                && await CountProjectsRealtimeDeliveryAsync(messageId) == 1
                && await ScalarLongAsync("SELECT count(*) FROM infrastructure.module_event_deliveries WHERE \"MessageId\"=@message AND \"ConsumerKey\"=@consumer AND \"State\"='completed'",
                    ("message", messageId), ("consumer", ProjectsRealtimeConsumerKey)) == 1
                && await CountProjectsRealtimeHistoryAsync(organizationId, messageId) == 1)
                return;
            await Task.Delay(50, deadline.Token);
        }
    }

    public async Task<SourceEvidence> ReadProjectSourceAsync(Guid organizationId)
    {
        await using NpgsqlConnection connection = new(owner);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("""
            SELECT "Id"::text, "Type", "Payload"::text, "ContextJson"::text
            FROM platform.outbox_messages
            ORDER BY "OccurredAt", "Id"
            """, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Guid messageId = Guid.Empty;
        int typedCount = 0;
        int legacyCount = 0;
        ProjectCreatedPayload payload = default!;
        OutboxContext context = default!;
        while (await reader.ReadAsync())
        {
            string type = reader.GetString(1);
            if (type == "projects.created")
            {
                typedCount++;
                messageId = Guid.Parse(reader.GetString(0));
                payload = JsonSerializer.Deserialize<ProjectCreatedPayload>(reader.GetString(2), WebJson)!;
                context = JsonSerializer.Deserialize<OutboxContext>(reader.GetString(3), WebJson)!;
            }
            if (type == "TrykatchApp.Application.Projects.ProjectChanged") legacyCount++;
        }
        messageId.ShouldNotBe(Guid.Empty);
        payload.OrganizationId.ShouldBe(organizationId);
        return new(messageId, legacyCount, typedCount, payload, context);
    }

    public static ProducerEvidence ReadProducerEvidence(string output)
    {
        string line = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .SingleOrDefault(value => value.Contains("producer-committed", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Producer process did not report its committed work IDs.");
        return JsonSerializer.Deserialize<ProducerEvidence>(line, WebJson)
            ?? throw new InvalidOperationException("Producer process reported invalid completion metadata.");
    }

    public async Task<Guid> ReadDeliveryIdAsync(Guid messageId)
    {
        await using NpgsqlConnection connection = new(owner);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("SELECT \"Id\"::text FROM infrastructure.module_event_deliveries WHERE \"MessageId\"=@message AND \"ConsumerKey\"=@consumer", connection);
        command.Parameters.AddWithValue("message", messageId);
        command.Parameters.AddWithValue("consumer", AcceptanceModule.ConsumerKey);
        return Guid.Parse((string)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Expected event delivery row is missing.")));
    }

    public async Task WaitForCompletionAsync(Guid messageId, Guid organizationId, Guid targetId, Guid jobWorkId)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(45));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (await CountEventInboxAsync(organizationId, messageId) == 1
                && await CountEventDeliveriesAsync(messageId) == 1
                && await CountProjectsRealtimeInboxAsync(organizationId, messageId) == 1
                && await CountProjectsRealtimeDeliveryAsync(messageId) == 1
                && await CountProjectsRealtimeHistoryAsync(organizationId, messageId) == 1
                && await ScalarLongAsync("SELECT count(*) FROM infrastructure.module_event_deliveries WHERE \"MessageId\"=@message AND \"ConsumerKey\"=@consumer AND \"State\"='completed'",
                    ("message", messageId), ("consumer", ProjectsRealtimeConsumerKey)) == 1
                && await ScalarLongAsync("SELECT count(*) FROM infrastructure.module_work_items WHERE \"Id\"=@work AND \"State\"='completed'",
                    ("work", jobWorkId)) == 1
                && await ScalarLongAsync("SELECT count(*) FROM app.execution_acceptance_effects WHERE \"Id\"=@target AND \"CompletedAtUtc\" IS NOT NULL",
                    ("target", targetId)) == 1)
                return;
            await Task.Delay(50, deadline.Token);
        }
    }

    public async Task WaitForWorkStateAsync(Guid workId, string state)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        while (await ScalarLongAsync("SELECT count(*) FROM infrastructure.module_work_items WHERE \"Id\"=@id AND \"State\"=@state",
                   ("id", workId), ("state", state)) != 1)
            await Task.Delay(50, deadline.Token);
    }

    public async Task WaitForAcknowledgementRecoveryAsync(Guid organizationId, Guid messageId, Guid workId)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(45));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (await CountEventDeliveriesAsync(messageId) == 1
                && await ScalarLongAsync("SELECT count(*) FROM infrastructure.module_event_deliveries WHERE \"MessageId\"=@message AND \"ConsumerKey\"=@consumer AND \"State\"='completed'",
                    ("message", messageId), ("consumer", AcceptanceModule.ConsumerKey)) == 1
                && await ScalarLongAsync("SELECT count(*) FROM infrastructure.module_event_deliveries WHERE \"MessageId\"=@message AND \"ConsumerKey\"=@consumer AND \"State\"='completed'",
                    ("message", messageId), ("consumer", ProjectsRealtimeConsumerKey)) == 1
                && await CountProjectsRealtimeInboxAsync(organizationId, messageId) == 1
                && await CountProjectsRealtimeHistoryAsync(organizationId, messageId) == 1
                && await ScalarLongAsync("SELECT count(*) FROM infrastructure.module_work_items WHERE \"Id\"=@work AND \"State\"='completed'",
                    ("work", workId)) == 1)
                return;
            await Task.Delay(50, deadline.Token);
        }
    }

    public async Task<EventEffectEvidence> ReadEventEffectAsync(Guid organizationId, Guid messageId)
    {
        await using NpgsqlConnection connection = new(owner);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("""
            SELECT "ProjectId"::text, "EffectiveActorId"::text, "OriginActorId"::text
            FROM app.execution_acceptance_effects
            WHERE "OrganizationId"=@organization AND "EventMessageId"=@message AND "Kind"=@kind
            """, connection);
        command.Parameters.AddWithValue("organization", organizationId);
        command.Parameters.AddWithValue("message", messageId);
        command.Parameters.AddWithValue("kind", AcceptanceEffect.ProjectEvent);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        return new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)));
    }

    public async Task<JobEffectEvidence> ReadJobEffectAsync(Guid organizationId, Guid targetId)
    {
        await using NpgsqlConnection connection = new(owner);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("""
            SELECT "EffectiveActorId"::text, "OriginActorId"::text, "CompletedAtUtc" IS NOT NULL
            FROM app.execution_acceptance_effects WHERE "OrganizationId"=@organization AND "Id"=@target
            """, connection);
        command.Parameters.AddWithValue("organization", organizationId);
        command.Parameters.AddWithValue("target", targetId);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        return new(reader.IsDBNull(0) ? null : Guid.Parse(reader.GetString(0)),
            reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)), reader.GetBoolean(2));
    }

    public Task<string> ReadCompletionAuditAsync(Guid organizationId, Guid workId, Guid actorId) => ReadAuditAsync(
        organizationId, "module.work.completed", "module-work", workId, actorId);

    public Task<string> ReadEventAuditAsync(Guid organizationId, Guid messageId, Guid actorId, Guid originActorId) => ReadAuditAsync(
        organizationId, "module.event.completed", "module-event-delivery", messageId, actorId, originActorId);

    public async Task AssertRuntimeIsolationAsync(Guid organizationId, Guid actorId)
    {
        Organization other = Organization.Create("Other organization", $"other-{Guid.NewGuid():N}"[..30]);
        Guid foreignTarget = await SeedSyntheticTargetAsync(other.Id, actorId);
        await using (PlatformDbContext platform = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(owner).Options))
        {
            OrganizationDataPlacementRecord placement = OrganizationDataPlacementRecord.Begin(
                other.Id, OrganizationDataPlacementKind.Shared, "postgres", null);
            placement.MarkReady(new NpgsqlConnectionStringBuilder(owner).Database!, "integration-test", "baseline", DateTimeOffset.UtcNow);
            platform.Add(other);
            platform.Add(placement);
            await platform.SaveChangesAsync();
        }

        await using NpgsqlConnection runtime = new(organizationRuntime);
        await runtime.OpenAsync();
        await using NpgsqlTransaction transaction = await runtime.BeginTransactionAsync();
        await using (NpgsqlCommand scope = new("SELECT set_config('app.organization_id', @organization, true), set_config('app.actor_id', @actor, true)", runtime, transaction))
        {
            scope.Parameters.AddWithValue("organization", organizationId.ToString());
            scope.Parameters.AddWithValue("actor", actorId.ToString());
            await scope.ExecuteNonQueryAsync();
        }
        await using NpgsqlCommand probe = new("SELECT count(*) FROM app.execution_acceptance_effects WHERE \"Id\"=@foreign", runtime, transaction);
        probe.Parameters.AddWithValue("foreign", foreignTarget);
        Convert.ToInt64(await probe.ExecuteScalarAsync(), CultureInfo.InvariantCulture).ShouldBe(0);
        await using NpgsqlCommand mutation = new(
            "UPDATE app.execution_acceptance_effects SET \"CompletedAtUtc\"=CURRENT_TIMESTAMP WHERE \"Id\"=@foreign", runtime, transaction);
        mutation.Parameters.AddWithValue("foreign", foreignTarget);
        (await mutation.ExecuteNonQueryAsync()).ShouldBe(0);
        await transaction.CommitAsync();
    }

    public async Task AssertInstalledResourceAndRoleSafetyAsync()
    {
        await using NpgsqlConnection connection = new(owner);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("""
            SELECT r.rolname,
                   r.rolsuper OR r.rolbypassrls OR r.rolcreatedb OR r.rolcreaterole OR r.rolreplication,
                   c.relforcerowsecurity,
                   EXISTS (SELECT 1 FROM platform.module_data_resources WHERE schema_name='app'
                       AND table_name='execution_acceptance_effects' AND module_id=@module),
                   pg_get_userbyid(c.relowner)=r.rolname,
                   has_table_privilege(r.rolname,c.oid,'SELECT')
            FROM pg_roles r
            JOIN pg_class c ON c.relname='execution_acceptance_effects'
            JOIN pg_namespace n ON n.oid=c.relnamespace AND n.nspname='app'
            WHERE r.rolname=ANY(@roles)
            """, connection);
        command.Parameters.AddWithValue("module", AcceptanceModule.Id);
        command.Parameters.AddWithValue("roles", RuntimeRoles);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        List<string> inspected = [];
        while (await reader.ReadAsync())
        {
            string role = reader.GetString(0);
            inspected.Add(role);
            reader.GetBoolean(1).ShouldBeFalse();
            reader.GetBoolean(2).ShouldBeTrue();
            reader.GetBoolean(3).ShouldBeTrue();
            reader.GetBoolean(4).ShouldBeFalse();
            reader.GetBoolean(5).ShouldBe(role == PostgresRuntimeRoleFixture.OrganizationRole);
        }
        inspected.Order(StringComparer.Ordinal).ShouldBe(RuntimeRoles.Order(StringComparer.Ordinal));
    }

    private async Task<string> ReadAuditAsync(Guid organizationId, string operation, string subjectType, Guid subjectId,
        Guid actorId, Guid? originActorId = null)
    {
        await using NpgsqlConnection connection = new(owner);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("""
            SELECT "Details"::text FROM platform.audit_intents
            WHERE "OrganizationId"=@organization AND "Operation"=@operation AND "SubjectType"=@subjectType
              AND "SubjectId"=@subject AND "ActorId"=@actor
            ORDER BY "OccurredAt" DESC LIMIT 1
            """, connection);
        command.Parameters.AddWithValue("organization", organizationId);
        command.Parameters.AddWithValue("operation", operation);
        command.Parameters.AddWithValue("subjectType", subjectType);
        command.Parameters.AddWithValue("subject", subjectId.ToString());
        command.Parameters.AddWithValue("actor", actorId);
        string result = (string?)await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Expected execution audit intent is missing.");
        if (originActorId is { } origin) result.ShouldContain(origin.ToString());
        return result;
    }

    private async Task<long> ScalarLongAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using NpgsqlConnection connection = new(owner);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        foreach ((string name, object value) in parameters) command.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private async Task<int> ExecuteOwnerAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using NpgsqlConnection connection = new(owner);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        foreach ((string name, object value) in parameters) command.Parameters.AddWithValue(name, value);
        return await command.ExecuteNonQueryAsync();
    }

    private ProcessStartInfo CreateChildStart(string mode)
    {
        string testAssembly = Assembly.GetExecutingAssembly().Location;
        string helperAssembly = typeof(ExecutionAcceptanceProcessMarker).Assembly.Location;
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(dotnet) || !File.Exists(dotnet))
        {
            string? dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            dotnet = !string.IsNullOrWhiteSpace(dotnetRoot) && File.Exists(Path.Combine(dotnetRoot, "dotnet"))
                ? Path.Combine(dotnetRoot, "dotnet")
                : "dotnet";
        }
        ProcessStartInfo start = new(dotnet)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(Path.ChangeExtension(testAssembly, ".runtimeconfig.json"));
        start.ArgumentList.Add("--depsfile");
        start.ArgumentList.Add(Path.ChangeExtension(testAssembly, ".deps.json"));
        start.ArgumentList.Add(helperAssembly);
        start.ArgumentList.Add(mode);

        Dictionary<string, string?> safeEnvironment = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Where(entry => entry.Key is string key
                && (key == "PATH" || key == "HOME" || key.StartsWith("DOTNET_", StringComparison.Ordinal)
                    || key is "TMPDIR" or "TMP" or "TEMP"))
            .ToDictionary(entry => (string)entry.Key!, entry => (string?)entry.Value, StringComparer.Ordinal);
        start.Environment.Clear();
        foreach ((string name, string? value) in safeEnvironment)
            if (value is not null) start.Environment[name] = value;
        foreach ((string name, string value) in runtimeConnections)
            start.Environment["ConnectionStrings__" + name] = value;
        start.Environment["Jobs__Enabled"] = "true";
        start.Environment["Email__Host"] = "127.0.0.1";
        start.Environment["Email__Port"] = "1";
        start.Environment["Email__From"] = "execution-acceptance@example.test";
        start.Environment["Email__Security"] = "StartTls";
        return start;
    }

    private static async Task ApplyModuleMigrationsAsync(string connectionString, IReadOnlyList<IModule> modules)
    {
        foreach (PendingModuleMigration migration in ModuleMigrationPlan.Build(modules, []))
        {
            await using NpgsqlConnection connection = new(connectionString);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new(migration.Sql, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task CreateAcceptanceEffectRelationAsync(string owner)
    {
        await using NpgsqlConnection connection = new(owner);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("""
            CREATE TABLE app.execution_acceptance_effects (
                "Id" uuid PRIMARY KEY,
                "OrganizationId" uuid NOT NULL,
                "Kind" character varying(40) NOT NULL,
                "ActorId" uuid NOT NULL,
                "EventMessageId" uuid NULL,
                "ProjectId" uuid NULL,
                "EffectiveActorId" uuid NULL,
                "OriginActorId" uuid NULL,
                "CompletedAtUtc" timestamp with time zone NULL);
            CREATE INDEX execution_acceptance_effects_organization_id
                ON app.execution_acceptance_effects ("OrganizationId", "Id");
            CREATE UNIQUE INDEX execution_acceptance_effects_event_message
                ON app.execution_acceptance_effects ("OrganizationId", "EventMessageId") WHERE "EventMessageId" IS NOT NULL;
            ALTER TABLE app.execution_acceptance_effects ENABLE ROW LEVEL SECURITY;
            ALTER TABLE app.execution_acceptance_effects FORCE ROW LEVEL SECURITY;
            CREATE POLICY execution_acceptance_effects_organization ON app.execution_acceptance_effects
                USING ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid)
                WITH CHECK ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid);
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static RuntimeDatabaseRoles ToRuntimeRoles() => new(
        PostgresRuntimeRoleFixture.OrganizationRole,
        PostgresRuntimeRoleFixture.PlatformRole,
        PostgresRuntimeRoleFixture.IdentityRole,
        PostgresRuntimeRoleFixture.OutboxRole,
        PostgresRuntimeRoleFixture.WorkCoordinatorRole);

    private static string RoleConnection(string ownerConnection, string role, string password) =>
        new NpgsqlConnectionStringBuilder(ownerConnection) { Username = role, Password = password }.ConnectionString;

    public async ValueTask DisposeAsync()
    {
        foreach (WorkerProcess worker in workers)
            await worker.DisposeAsync();
        await parentServices.DisposeAsync();
        await postgres.DisposeAsync();
    }

}

internal sealed record AcceptedProjectsWork(Guid WorkId, Guid OrganizationId, Guid ActorId, Guid MembershipId);
internal sealed record ProcessResult(int Exit, string Output, string Error);
internal sealed record ProjectCreatedPayload(Guid OrganizationId, Guid ProjectId, Guid ActorId);
internal sealed record SourceEvidence(Guid MessageId, int LegacyCount, int TypedCount, ProjectCreatedPayload Payload, OutboxContext Context);
internal sealed record ProducerEvidence(string Kind, int ProcessId, Guid ProjectWorkId, string ProjectState, Guid JobWorkId,
    string JobState, Guid OrganizationId, Guid ActorId);
internal sealed record EventEffectEvidence(Guid ProjectId, Guid EffectiveActorId, Guid OriginActorId);
internal sealed record JobEffectEvidence(Guid? EffectiveActorId, Guid? OriginActorId, bool Completed);
internal sealed record ProjectRealtimeEvidence(Guid EventId, Guid SourceMessageId, string ModuleId, string EventKey,
    int SchemaVersion, string PolicyOwnerModuleId, string PolicyKey, int PolicyVersion, int AudienceKind,
    Guid? UserId, string? ResourceKey, Guid? ResourceId);
internal sealed record WorkerReady(string Kind, int ProcessId, string Instance, string SchedulerInstanceId,
    string OrganizationRole, string OutboxRole, string WorkCoordinatorRole);

internal sealed class WorkerProcess(Process process, WorkerReady ready) : IAsyncDisposable
{
    private bool stopped;
    public WorkerReady Ready { get; } = ready;

    public async Task EnsureRunningAsync()
    {
        if (process.HasExited) await StopAsync();
    }

    public async Task StopAsync()
    {
        if (stopped) return;
        stopped = true;
        try
        {
            bool unexpectedlyExited = process.HasExited;
            if (!unexpectedlyExited) process.Kill(entireProcessTree: true);
            (int exit, string output, string error) = await ProcessExecution.WaitAsync(process, TimeSpan.FromSeconds(10));
            if (unexpectedlyExited)
                throw new InvalidOperationException($"Worker {Ready.Instance} exited before requested shutdown with code {exit}. {output}{error}");
            exit.ShouldNotBe(0, output + error);
        }
        finally { process.Dispose(); }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
