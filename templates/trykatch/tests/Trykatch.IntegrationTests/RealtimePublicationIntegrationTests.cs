using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Application;
using Trykatch.Application.Organizations;
using Trykatch.Application.Realtime;
using Trykatch.Infrastructure;
using Trykatch.Infrastructure.Execution;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Infrastructure.Realtime;
using Trykatch.Modules;
using Trykatch.Modules.Projects.Infrastructure;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class RealtimePublicationIntegrationTests
{
    private const string PostgresImage =
        "postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f";
    private static readonly Guid SystemActorId = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private const string PolicyKey = "projects.realtime-test-audience";
    private const string OperationKey = "projects.realtime-test";
    private const string GrantKey = "projects.realtime-test-grant";

    [TestMethod]
    public async Task CommittedPublicationAppearsAsTypedReferenceHistory()
    {
        await using RealtimeFixture fixture = await RealtimeFixture.StartAsync();
        Guid organizationId = Guid.NewGuid();
        Guid sourceMessageId = Guid.NewGuid();
        Guid eventId = Guid.NewGuid();
        ModuleRealtimeNotification notification = Notification(eventId);

        await using (PublicationScope publish = await fixture.BeginPublicationScopeAsync(organizationId))
        {
            InitializeExecution(publish, sourceMessageId);
            await publish.Services.GetRequiredService<IModuleRealtimePublisher>().PublishAsync(notification);
            RealtimeHistoryStore store = publish.Services.GetRequiredService<RealtimeHistoryStore>();
            (await store.ReadHeadAsync()).ShouldBe(1L);
            await publish.Transaction.CommitAsync(CancellationToken.None);
        }

        await using PublicationScope read = await fixture.BeginReadScopeAsync(organizationId);
        RealtimeHistoryStore history = read.Services.GetRequiredService<RealtimeHistoryStore>();
        (await history.ReadHeadAsync()).ShouldBe(1L);
        RealtimeHistoryPage page = await history.ReadPageAsync(0);
        page.Position.ShouldBe(1L);
        page.HasMore.ShouldBeFalse();
        page.Items.Count.ShouldBe(1);
        ModuleRealtimeNotification delivered = page.Items.Single().ToNotification();
        delivered.ModuleId.ShouldBe("projects");
        delivered.EventKey.ShouldBe("projects.created");
        delivered.SchemaVersion.ShouldBe(1);
        delivered.EventId.ShouldBe(eventId);
        delivered.Audience.Kind.ShouldBe(ModuleRealtimeAudienceKind.Organization);
        delivered.Audience.Policy.ShouldBe(PolicyReference);
    }

    [TestMethod]
    public async Task RecipientHistoryAdvancesPastFullyFilteredPagesAndRechecksCurrentResourceVisibility()
    {
        await using RealtimeFixture fixture = await RealtimeFixture.StartAsync(resourceVisibilityPolicy: true);
        Guid organizationId = Guid.NewGuid();
        Guid visibleProject = Guid.NewGuid();
        Guid visibleEvent = Guid.NewGuid();
        await using (PublicationScope publish = await fixture.BeginPublicationScopeAsync(organizationId))
        {
            InitializeExecution(publish, Guid.NewGuid());
            await InsertProjectAsync(publish, organizationId, "visible-realtime-project", visibleProject);
            IModuleRealtimePublisher publisher = publish.Services.GetRequiredService<IModuleRealtimePublisher>();
            for (int index = 0; index < 101; index++)
            {
                Guid project = index == 100 ? visibleProject : Guid.NewGuid();
                ModuleRealtimeNotification notification = Notification(index == 100 ? visibleEvent : Guid.NewGuid()) with
                {
                    Audience = new(PolicyReference, ModuleRealtimeAudienceKind.Resource, Resource: new("projects", project))
                };
                await publisher.PublishAsync(notification);
            }
            await publish.Transaction.CommitAsync(CancellationToken.None);
        }
        await using (PublicationScope read = await fixture.BeginReadScopeAsync(organizationId))
        {
            IRealtimeHistoryReader history = read.Services.GetRequiredService<IRealtimeHistoryReader>();
            (await history.ReadHeadAsync()).ShouldBe(101L);
            RealtimeHistoryBatch first = await history.ReadPageAsync(0);
            first.Items.ShouldBeEmpty();
            first.Position.ShouldBe(100L);
            first.HasMore.ShouldBeTrue();
        }
        await using (PublicationScope read = await fixture.BeginReadScopeAsync(organizationId))
        {
            RealtimeHistoryBatch last = await read.Services.GetRequiredService<IRealtimeHistoryReader>().ReadPageAsync(100);
            last.Position.ShouldBe(101L);
            last.HasMore.ShouldBeFalse();
            last.Items.Count.ShouldBe(1);
            last.Items.Single().Position.ShouldBe(101L);
            last.Items.Single().Notification.EventId.ShouldBe(visibleEvent);
            last.Items.Single().Notification.Audience.Resource!.ResourceId.ShouldBe(visibleProject);
        }
        await using (PublicationScope change = await fixture.BeginPublicationScopeAsync(organizationId))
        {
            await change.Services.GetRequiredService<ApplicationDbContext>().Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM app.projects WHERE \"Id\" = {visibleProject}");
            await change.Transaction.CommitAsync(CancellationToken.None);
        }
        await using (PublicationScope read = await fixture.BeginReadScopeAsync(organizationId))
        {
            IRealtimeHistoryReader history = read.Services.GetRequiredService<IRealtimeHistoryReader>();
            RealtimeHistoryBatch nowPrivate = await history.ReadPageAsync(100);
            nowPrivate.Items.ShouldBeEmpty();
            nowPrivate.Position.ShouldBe(101L);
            nowPrivate.HasMore.ShouldBeFalse();
            await Should.ThrowAsync<InvalidOperationException>(() => history.ReadPageAsync(102));
        }
    }

    [TestMethod]
    public async Task HistoryAndBusinessWriteRemainInvisibleUntilCommitAndRollbackTogether()
    {
        await using RealtimeFixture fixture = await RealtimeFixture.StartAsync();
        Guid organizationId = Guid.NewGuid();
        const string committedName = "realtime-committed-project";
        const string rolledBackName = "realtime-rolled-back-project";

        await using (PublicationScope publish = await fixture.BeginPublicationScopeAsync(organizationId))
        {
            InitializeExecution(publish, Guid.NewGuid());
            await InsertProjectAsync(publish, organizationId, committedName);
            await publish.Services.GetRequiredService<IModuleRealtimePublisher>().PublishAsync(Notification(Guid.NewGuid()));

            await using PublicationScope concurrentRead = await fixture.BeginReadScopeAsync(organizationId);
            RealtimeHistoryStore history = concurrentRead.Services.GetRequiredService<RealtimeHistoryStore>();
            (await history.ReadHeadAsync()).ShouldBe(0L);
            (await CountProjectsAsync(concurrentRead, organizationId, committedName)).ShouldBe(0L);
            await publish.Transaction.CommitAsync(CancellationToken.None);
        }

        await using (PublicationScope publish = await fixture.BeginPublicationScopeAsync(organizationId))
        {
            InitializeExecution(publish, Guid.NewGuid());
            await InsertProjectAsync(publish, organizationId, rolledBackName);
            await publish.Services.GetRequiredService<IModuleRealtimePublisher>().PublishAsync(Notification(Guid.NewGuid()));
        }

        await using PublicationScope verify = await fixture.BeginReadScopeAsync(organizationId);
        RealtimeHistoryStore verifiedHistory = verify.Services.GetRequiredService<RealtimeHistoryStore>();
        (await verifiedHistory.ReadHeadAsync()).ShouldBe(1L);
        (await CountProjectsAsync(verify, organizationId, committedName)).ShouldBe(1L);
        (await CountProjectsAsync(verify, organizationId, rolledBackName)).ShouldBe(0L);
    }

    [TestMethod]
    public async Task IdenticalEventIdentityIsIdempotentAndChangedContentConflicts()
    {
        await using RealtimeFixture fixture = await RealtimeFixture.StartAsync();
        Guid organizationId = Guid.NewGuid();
        Guid sourceMessageId = Guid.NewGuid();
        ModuleRealtimeNotification notification = Notification(Guid.NewGuid());

        await using (PublicationScope first = await fixture.BeginPublicationScopeAsync(organizationId))
        {
            InitializeExecution(first, sourceMessageId);
            await first.Services.GetRequiredService<IModuleRealtimePublisher>().PublishAsync(notification);
            await first.Transaction.CommitAsync(CancellationToken.None);
        }

        await using (PublicationScope repeated = await fixture.BeginPublicationScopeAsync(organizationId))
        {
            InitializeExecution(repeated, sourceMessageId);
            await repeated.Services.GetRequiredService<IModuleRealtimePublisher>().PublishAsync(notification);
            await repeated.Transaction.CommitAsync(CancellationToken.None);
        }

        await using PublicationScope verify = await fixture.BeginReadScopeAsync(organizationId);
        RealtimeHistoryStore history = verify.Services.GetRequiredService<RealtimeHistoryStore>();
        (await history.ReadHeadAsync()).ShouldBe(1L);

        await using PublicationScope conflicting = await fixture.BeginPublicationScopeAsync(organizationId);
        InitializeExecution(conflicting, sourceMessageId);
        ModuleRealtimeNotification changed = notification with
        {
            Audience = new(PolicyReference, ModuleRealtimeAudienceKind.User, Guid.NewGuid())
        };
        await Should.ThrowAsync<InvalidOperationException>(() =>
            conflicting.Services.GetRequiredService<IModuleRealtimePublisher>().PublishAsync(changed));
    }

    [TestMethod]
    public async Task SeparateOrganizationsHaveIndependentHistoryPositions()
    {
        await using RealtimeFixture fixture = await RealtimeFixture.StartAsync();
        Guid firstOrganization = Guid.NewGuid();
        Guid secondOrganization = Guid.NewGuid();
        Guid sharedEventId = Guid.NewGuid();
        Guid sharedSourceId = Guid.NewGuid();

        foreach (Guid organizationId in new[] { firstOrganization, secondOrganization })
        {
            await using PublicationScope publish = await fixture.BeginPublicationScopeAsync(organizationId);
            InitializeExecution(publish, sharedSourceId);
            await publish.Services.GetRequiredService<IModuleRealtimePublisher>()
                .PublishAsync(Notification(sharedEventId));
            await publish.Transaction.CommitAsync(CancellationToken.None);
        }

        foreach (Guid organizationId in new[] { firstOrganization, secondOrganization })
        {
            await using PublicationScope read = await fixture.BeginReadScopeAsync(organizationId);
            RealtimeHistoryStore history = read.Services.GetRequiredService<RealtimeHistoryStore>();
            (await history.ReadHeadAsync()).ShouldBe(1L);
            (await history.ReadPageAsync(0)).Items.Count.ShouldBe(1);
        }
    }

    [TestMethod]
    public async Task CompetingFirstPublicationWaitsAndSequenceFollowsCommitOrder()
    {
        await using RealtimeFixture fixture = await RealtimeFixture.StartAsync();
        Guid organizationId = Guid.NewGuid();
        PublicationScope first = await fixture.BeginPublicationScopeAsync(organizationId);
        await using (first)
        {
            Guid firstEvent = Guid.NewGuid();
            InitializeExecution(first, Guid.NewGuid());
            await first.Services.GetRequiredService<IModuleRealtimePublisher>().PublishAsync(Notification(firstEvent));

            Task competing = Task.Run(async () =>
            {
                await using PublicationScope second = await fixture.BeginPublicationScopeAsync(organizationId);
                InitializeExecution(second, Guid.NewGuid());
                await second.Services.GetRequiredService<IModuleRealtimePublisher>()
                    .PublishAsync(Notification(Guid.NewGuid()));
                await second.Transaction.CommitAsync(CancellationToken.None);
            });
            await fixture.WaitForRealtimeStreamLockWaiterAsync();
            await first.Transaction.CommitAsync(CancellationToken.None);
            await competing.WaitAsync(TimeSpan.FromSeconds(10));
        }

        await using PublicationScope read = await fixture.BeginReadScopeAsync(organizationId);
        RealtimeHistoryPage page = await read.Services.GetRequiredService<RealtimeHistoryStore>().ReadPageAsync(0);
        page.Items.Count.ShouldBe(2);
        page.Position.ShouldBe(2L);
        page.Items[0].Sequence.ShouldBe(1L);
        page.Items[1].Sequence.ShouldBe(2L);
    }

    [TestMethod]
    public async Task HistoryPagesStopAtOneHundredScannedRowsAndAdvanceThePosition()
    {
        await using RealtimeFixture fixture = await RealtimeFixture.StartAsync();
        Guid organizationId = Guid.NewGuid();
        await using (PublicationScope publish = await fixture.BeginPublicationScopeAsync(organizationId))
        {
            IModuleRealtimePublisher publisher = publish.Services.GetRequiredService<IModuleRealtimePublisher>();
            InitializeExecution(publish, Guid.NewGuid());
            for (int index = 0; index < 101; index++)
            {
                await publisher.PublishAsync(Notification(Guid.NewGuid()));
            }
            await publish.Transaction.CommitAsync(CancellationToken.None);
        }

        await using PublicationScope read = await fixture.BeginReadScopeAsync(organizationId);
        RealtimeHistoryStore history = read.Services.GetRequiredService<RealtimeHistoryStore>();
        RealtimeHistoryPage first = await history.ReadPageAsync(0);
        first.Items.Count.ShouldBe(100);
        first.Position.ShouldBe(100L);
        first.HasMore.ShouldBeTrue();
        RealtimeHistoryPage second = await history.ReadPageAsync(first.Position);
        second.Items.Count.ShouldBe(1);
        second.Position.ShouldBe(101L);
        second.HasMore.ShouldBeFalse();
        RealtimeHistoryPage terminal = await history.ReadPageAsync(second.Position);
        terminal.Items.ShouldBeEmpty();
        terminal.Position.ShouldBe(101L);
        terminal.HasMore.ShouldBeFalse();
    }

    [TestMethod]
    public async Task HistoryReaderRequiresCurrentActorScopedOrganizationTransaction()
    {
        await using RealtimeFixture fixture = await RealtimeFixture.StartAsync();
        Guid organizationId = Guid.NewGuid();

        await using (PublicationScope writable = await fixture.BeginPublicationScopeAsync(organizationId))
        {
            RealtimeHistoryStore history = writable.Services.GetRequiredService<RealtimeHistoryStore>();
            await Should.ThrowAsync<InvalidOperationException>(() => history.ReadPageAsync(0));
            await Should.ThrowAsync<InvalidOperationException>(() => writable.Services.GetRequiredService<IRealtimeHistoryReader>().ReadPageAsync(0));
        }

        await using (PublicationScope unshared = await fixture.BeginPublicationScopeAsync(organizationId, enlist: false))
        {
            RealtimeHistoryStore history = unshared.Services.GetRequiredService<RealtimeHistoryStore>();
            await Should.ThrowAsync<InvalidOperationException>(() => history.ReadHeadAsync());
            await Should.ThrowAsync<InvalidOperationException>(() => unshared.Services.GetRequiredService<IRealtimeHistoryReader>().ReadHeadAsync());
        }

        await using (PublicationScope read = await fixture.BeginReadScopeAsync(organizationId))
        {
            RealtimeHistoryStore history = read.Services.GetRequiredService<RealtimeHistoryStore>();
            await Should.ThrowAsync<InvalidOperationException>(() => history.ReadPageAsync(-1));
            await Should.ThrowAsync<InvalidOperationException>(() => history.ReadPageAsync(long.MaxValue));
            IRealtimeHistoryReader recipient = read.Services.GetRequiredService<IRealtimeHistoryReader>();
            await Should.ThrowAsync<InvalidOperationException>(() => recipient.ReadPageAsync(-1));
            await Should.ThrowAsync<InvalidOperationException>(() => recipient.ReadPageAsync(long.MaxValue));

            string wrongActor = Guid.NewGuid().ToString();
            await read.Services.GetRequiredService<OrganizationControlPlaneDbContext>().Database
                .ExecuteSqlInterpolatedAsync($"SELECT set_config('app.actor_id', {wrongActor}, true)");
            await Should.ThrowAsync<InvalidOperationException>(() => history.ReadPageAsync(0));
            await Should.ThrowAsync<InvalidOperationException>(() => recipient.ReadPageAsync(0));
        }
    }

    [TestMethod]
    public async Task PublicationFailsClosedForMissingOrForgedExecutionAuthority()
    {
        await using RealtimeFixture fixture = await RealtimeFixture.StartAsync();
        string[] cases =
        [
            "missing-organization-context", "missing-shared-transaction", "wrong-organization", "empty-organization",
            "wrong-actor", "empty-actor", "wrong-guc", "missing-execution", "wrong-catalog", "wrong-grant",
            "wrong-grant-version", "wrong-operation-version", "user-command", "scheduled", "missing-source",
            "unknown-policy", "unknown-resource", "unknown-event"
        ];

        foreach (string testCase in cases)
            await AssertPublicationRejectedAsync(fixture, testCase);
    }

    [TestMethod]
    public async Task ReadOnlyOrganizationScopeCannotPublishHistory()
    {
        await using RealtimeFixture fixture = await RealtimeFixture.StartAsync();
        await using PublicationScope publish = await fixture.BeginReadScopeAsync(Guid.NewGuid());
        InitializeExecution(publish, Guid.NewGuid());

        await Should.ThrowAsync<InvalidOperationException>(() =>
            publish.Services.GetRequiredService<IModuleRealtimePublisher>().PublishAsync(Notification(Guid.NewGuid())));
    }

    private static async Task AssertPublicationRejectedAsync(RealtimeFixture fixture, string testCase)
    {
        Guid organizationId = testCase == "empty-organization" ? Guid.Empty : Guid.NewGuid();
        Guid actorId = testCase == "empty-actor" ? Guid.Empty : SystemActorId;
        bool initializeOrganization = testCase != "missing-organization-context";
        bool enlist = testCase != "missing-shared-transaction";
        await using PublicationScope publish = await fixture.BeginPublicationScopeAsync(
            organizationId, actorId, initializeOrganization, enlist);

        if (testCase != "missing-execution")
        {
            ModuleExecutionKind executionKind = testCase switch
            {
                "user-command" => ModuleExecutionKind.UserCommand,
                "scheduled" => ModuleExecutionKind.ScheduledSystem,
                _ => ModuleExecutionKind.CommittedEvent
            };
            ModuleOperationDescriptor operation = testCase switch
            {
                "user-command" => publish.Services.GetRequiredService<ModuleCatalog>().Descriptors
                    .Single(module => module.Id == "projects").Operations.Single(candidate => candidate.OperationKey == "projects.create"),
                "scheduled" => RealtimeTestModule.Operation with { ExecutionKind = ModuleExecutionKind.ScheduledSystem },
                "wrong-operation-version" => RealtimeTestModule.Operation with { OperationVersion = 2 },
                _ => RealtimeTestModule.Operation
            };
            Guid? sourceMessageId = testCase == "missing-source" ? null : Guid.NewGuid();
            Guid identityOrganization = testCase == "wrong-organization" ? Guid.NewGuid() : organizationId;
            Guid identityActor = testCase == "wrong-actor" ? Guid.NewGuid() : actorId;
            string? grantKey = testCase == "wrong-grant" ? "projects.unknown-grant"
                : executionKind == ModuleExecutionKind.UserCommand ? null : GrantKey;
            int? grantVersion = executionKind == ModuleExecutionKind.UserCommand ? null
                : testCase == "wrong-grant-version" ? 2 : 1;
            int catalogVersion = testCase == "wrong-catalog" ? ModuleOperationRegistry.CatalogVersion + 1
                : ModuleOperationRegistry.CatalogVersion;
            publish.Services.GetRequiredService<ModuleWorkExecutionContext>().Initialize(
                new(identityOrganization, identityActor, null, null, executionKind, grantKey, grantVersion,
                    sourceMessageId, catalogVersion, null), operation);
        }

        if (testCase == "wrong-guc")
        {
            string wrongOrganization = Guid.NewGuid().ToString();
            await publish.Services.GetRequiredService<OrganizationControlPlaneDbContext>().Database
                .ExecuteSqlInterpolatedAsync($"SELECT set_config('app.organization_id', {wrongOrganization}, true)");
        }

        ModuleRealtimeNotification notification = testCase switch
        {
            "unknown-policy" => Notification(Guid.NewGuid()) with
            {
                Audience = new(new("projects", "projects.unknown-policy", 1), ModuleRealtimeAudienceKind.Organization)
            },
            "unknown-resource" => Notification(Guid.NewGuid()) with
            {
                Audience = new(PolicyReference, ModuleRealtimeAudienceKind.Resource,
                    Resource: new("documents", Guid.NewGuid()))
            },
            "unknown-event" => Notification(Guid.NewGuid()) with { EventKey = "projects.deleted" },
            _ => Notification(Guid.NewGuid())
        };

        await Should.ThrowAsync<InvalidOperationException>(() =>
            publish.Services.GetRequiredService<IModuleRealtimePublisher>().PublishAsync(notification));
    }

    private static async Task InsertProjectAsync(PublicationScope scope, Guid organizationId, string name, Guid? projectId = null)
    {
        await scope.Services.GetRequiredService<ApplicationDbContext>().Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO app.projects ("Id", "OrganizationId", "Name", "Description", "CreatedBy", "CreatedAt")
            VALUES ({projectId ?? Guid.NewGuid()}, {organizationId}, {name}, 'Realtime transaction fixture', {SystemActorId}, CURRENT_TIMESTAMP)
            """);
    }

    private static async Task<long> CountProjectsAsync(PublicationScope scope, Guid organizationId, string name)
    {
        await using NpgsqlCommand command = new("SELECT count(*) FROM app.projects WHERE \"OrganizationId\"=@organization AND \"Name\"=@name",
            (NpgsqlConnection)scope.Services.GetRequiredService<ApplicationDbContext>().Database.GetDbConnection(),
            (NpgsqlTransaction)scope.Services.GetRequiredService<ApplicationDbContext>().Database.CurrentTransaction!.GetDbTransaction());
        command.Parameters.AddWithValue("organization", organizationId);
        command.Parameters.AddWithValue("name", name);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static void InitializeExecution(PublicationScope scope, Guid sourceMessageId) =>
        scope.Services.GetRequiredService<ModuleWorkExecutionContext>().Initialize(
            new(scope.OrganizationId, scope.ActorId, null, null, ModuleExecutionKind.CommittedEvent,
                GrantKey, 1, sourceMessageId, ModuleOperationRegistry.CatalogVersion, null), RealtimeTestModule.Operation);

    private static ModuleRealtimeNotification Notification(Guid eventId) => new(
        "projects", "projects.created", 1, eventId,
        new(PolicyReference, ModuleRealtimeAudienceKind.Organization));

    private static ModuleRealtimePolicyRef PolicyReference => new("projects", PolicyKey, 1);

    private sealed class RealtimeTestModule : IModule
    {
        private readonly bool resourceVisibilityPolicy;

        public RealtimeTestModule(bool resourceVisibilityPolicy) =>
            this.resourceVisibilityPolicy = resourceVisibilityPolicy;

        public static ModuleOperationDescriptor Operation { get; } = new(
            "projects", OperationKey, 1, ModuleExecutionKind.CommittedEvent,
            "projects.realtime-test-payload", 1, [], "projects", GrantKey, 1024);

        private static ModuleRealtimeAudiencePolicyDescriptor Policy { get; } = new(
            "projects", PolicyKey, 1,
            [ModuleRealtimeAudienceKind.Organization, ModuleRealtimeAudienceKind.User, ModuleRealtimeAudienceKind.Resource],
            ["projects"], ["projects.read"], [new("projects.created", 1)],
            [new("projects", OperationKey, 1)]);

        private static ModuleSystemGrantDescriptor Grant { get; } = new(
            GrantKey, 1, "projects", SystemActorId, [OperationKey], ["projects"])
        {
            AllowedRealtimePolicies = [PolicyReference]
        };

        private static ModuleDescriptor ProjectsDescriptor { get; } = new ProjectsModule().Descriptor;
        private readonly ProjectsModule projects = new();

        public ModuleDescriptor Descriptor { get; } = ProjectsDescriptor with
        {
            Operations = [.. ProjectsDescriptor.Operations, Operation],
            SystemGrants = [.. ProjectsDescriptor.SystemGrants, Grant],
            RealtimeAudiencePolicies = [.. ProjectsDescriptor.RealtimeAudiencePolicies, Policy]
        };

        public void Register(IServiceCollection services, IConfiguration configuration)
        {
            projects.Register(services, configuration);
            services.AddScoped<ModuleRealtimeAudiencePolicyBinding>(provider =>
            {
                IModuleRealtimeAudiencePolicy policy = resourceVisibilityPolicy
                    ? new ResourceExistsAudiencePolicy(provider.GetRequiredService<ApplicationDbContext>())
                    : new AllowAllAudiencePolicy();
                return new(PolicyReference, policy);
            });
        }
    }

    private sealed class AllowAllAudiencePolicy : IModuleRealtimeAudiencePolicy
    {
        public Task<bool> AuthorizeAsync(ModuleRealtimeAudienceContext context, ModuleRealtimeAudience audience,
            CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class ResourceExistsAudiencePolicy(ApplicationDbContext database) : IModuleRealtimeAudiencePolicy
    {
        public Task<bool> AuthorizeAsync(ModuleRealtimeAudienceContext context, ModuleRealtimeAudience audience, CancellationToken cancellationToken) =>
            audience.Resource is { } resource
                ? database.Set<Project>().AsNoTracking().AnyAsync(project => project.Id == resource.ResourceId, cancellationToken)
                : Task.FromResult(false);
    }

    private sealed class PublicationScope(
        AsyncServiceScope scope,
        IOrganizationDataScope transaction,
        Guid organizationId,
        Guid actorId) : IAsyncDisposable
    {
        public IServiceProvider Services => scope.ServiceProvider;
        public IOrganizationDataScope Transaction { get; } = transaction;
        public Guid OrganizationId { get; } = organizationId;
        public Guid ActorId { get; } = actorId;

        public ValueTask DisposeAsync() => DisposeCoreAsync();

        private async ValueTask DisposeCoreAsync()
        {
            await Transaction.DisposeAsync();
            await scope.DisposeAsync();
        }
    }

    private sealed class RealtimeFixture(PostgreSqlContainer postgres, string ownerConnection, ServiceProvider services)
        : IAsyncDisposable
    {
        private readonly string owner = ownerConnection;
        private readonly ServiceProvider provider = services;

        public static async Task<RealtimeFixture> StartAsync(bool resourceVisibilityPolicy = false)
        {
            PostgreSqlContainer postgres = new PostgreSqlBuilder(PostgresImage).Build();
            await postgres.StartAsync();
            try
            {
                string owner = postgres.GetConnectionString();
                await PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(owner);
                (int exit, string output, string error) = await ModuleJobMigrationIntegrationTests.InvokeMigratorAsync(owner);
                exit.ShouldBe(0, error + output);
                (string organization, string platform, string identity, string outbox) =
                    await PostgresRuntimeRoleFixture.CreateConnectionStringsAsync(owner);
                string coordinator = RoleConnection(owner, PostgresRuntimeRoleFixture.WorkCoordinatorRole,
                    PostgresRuntimeRoleFixture.WorkCoordinatorPassword);
                Dictionary<string, string?> settings = new()
                {
                    [$"ConnectionStrings:{RuntimeDatabaseConnectionContract.Organization}"] = organization,
                    [$"ConnectionStrings:{RuntimeDatabaseConnectionContract.Platform}"] = platform,
                    [$"ConnectionStrings:{RuntimeDatabaseConnectionContract.Identity}"] = identity,
                    [$"ConnectionStrings:{RuntimeDatabaseConnectionContract.Outbox}"] = outbox,
                    [$"ConnectionStrings:{RuntimeDatabaseConnectionContract.WorkCoordinator}"] = coordinator
                };
                IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
                ServiceCollection services = new();
                services.AddSingleton(configuration);
                services.AddLogging();
                services.AddApplication();
                services.AddModules(configuration, [new RealtimeTestModule(resourceVisibilityPolicy)]);
                services.AddInfrastructure(configuration, isDevelopment: true, isOpenApiGeneration: true);
                ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
                return new(postgres, owner, provider);
            }
            catch
            {
                await postgres.DisposeAsync();
                throw;
            }
        }

        public async Task<PublicationScope> BeginPublicationScopeAsync(
            Guid? organizationId = null,
            Guid? actorId = null,
            bool initializeOrganization = true,
            bool enlist = true)
        {
            Guid organization = organizationId ?? Guid.NewGuid();
            Guid actor = actorId ?? SystemActorId;
            AsyncServiceScope scope = provider.CreateAsyncScope();
            try
            {
                IOrganizationDataScope transaction = await scope.ServiceProvider
                    .GetRequiredService<IOrganizationDataScopeFactory>().BeginAsync(organization, actor, CancellationToken.None);
                try
                {
                    if (enlist)
                        await OrganizationTransactionEnlistment.EnlistAsync(
                            scope.ServiceProvider.GetRequiredService<OrganizationControlPlaneDbContext>(),
                            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), organization, actor, CancellationToken.None);
                    if (initializeOrganization)
                        InitializeOrganizationContext(scope.ServiceProvider, organization, actor);
                    return new(scope, transaction, organization, actor);
                }
                catch
                {
                    await transaction.DisposeAsync();
                    throw;
                }
            }
            catch
            {
                await scope.DisposeAsync();
                throw;
            }
        }

        public async Task<PublicationScope> BeginReadScopeAsync(Guid organizationId)
        {
            AsyncServiceScope scope = provider.CreateAsyncScope();
            try
            {
                IOrganizationDataScope transaction = await scope.ServiceProvider
                    .GetRequiredService<IOrganizationDataScopeFactory>().BeginReadOnlyAsync(
                        organizationId, SystemActorId, CancellationToken.None);
                InitializeOrganizationContext(scope.ServiceProvider, organizationId, SystemActorId);
                return new(scope, transaction, organizationId, SystemActorId);
            }
            catch
            {
                await scope.DisposeAsync();
                throw;
            }
        }

        public async Task WaitForRealtimeStreamLockWaiterAsync()
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            do
            {
                await using NpgsqlConnection connection = new(owner);
                await connection.OpenAsync();
                await using NpgsqlCommand command = new("""
                    SELECT count(*) FROM pg_stat_activity
                    WHERE wait_event_type='Lock' AND query ILIKE '%realtime%'
                    """, connection);
                if ((long)(await command.ExecuteScalarAsync())! > 0) return;
                await Task.Delay(25);
            } while (DateTimeOffset.UtcNow < deadline);
            throw new TimeoutException("The competing realtime append never waited on the organization stream lock.");
        }

        public ValueTask DisposeAsync() => DisposeCoreAsync();

        private async ValueTask DisposeCoreAsync()
        {
            await provider.DisposeAsync();
            await postgres.DisposeAsync();
        }

        private static void InitializeOrganizationContext(IServiceProvider services, Guid organizationId, Guid actorId) =>
            services.GetRequiredService<IOrganizationContextInitializer>().Initialize(
                new OrganizationAccess(organizationId, "realtime-test", actorId, Guid.NewGuid(),
                    new HashSet<string>(["projects.read"], StringComparer.Ordinal)));

        private static string RoleConnection(string ownerConnection, string role, string password) =>
            new NpgsqlConnectionStringBuilder(ownerConnection) { Username = role, Password = password }.ConnectionString;
    }
}
