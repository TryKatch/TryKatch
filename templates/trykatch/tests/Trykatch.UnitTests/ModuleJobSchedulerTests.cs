using System.Text.Json;
using Trykatch.Infrastructure.Execution;
using Trykatch.Infrastructure.Jobs;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class ModuleJobSchedulerTests
{
    [TestMethod]
    public void RegistryResolvesOnlyInstalledJobVersionAndItsMatchingOperation()
    {
        ModuleOperationDescriptor operation = new(
            "orders", "orders.reconcile-work", 2, ModuleExecutionKind.UserCommand,
            "orders.reconcile", 1, [], "orders.write", null, 2048);
        ModuleJobDescriptor job = new(
            "orders", "orders.reconcile", 3, operation.OperationKey, operation.OperationVersion,
            operation.ExecutionKind, operation.PayloadSchemaKey, operation.PayloadSchemaVersion,
            operation.MaximumPayloadBytes);
        ModuleDescriptor descriptor = new("orders", "Orders", "1.0.0", "Test module", [], [],
            ModuleCapabilities.Api | ModuleCapabilities.BackgroundWork | ModuleCapabilities.Data, [])
        {
            DefaultDataOwnership = ModuleDataOwnership.Organization,
            Operations = [operation],
            Jobs = [job],
            DataResources = [new DataResourceDescriptor(
                "orders.write", "app", "orders_write", ModuleDataOwnership.Organization, IsolationPolicy: "orders-rls")]
        };
        ModuleCatalog catalog = new([new StubModule(descriptor)]);
        ModuleOperationRegistry operations = new(catalog, [new FakeHandler()], [new FakePolicy()]);

        ModuleJobRegistry registry = new(catalog, operations);

        registry.TryGet("orders.reconcile", 3, out ModuleJobRegistration? registration).ShouldBeTrue();
        registration!.Descriptor.ShouldBe(job);
        registration.Operation.Descriptor.ShouldBe(operation);
        registry.TryGet("orders.reconcile", 2, out _).ShouldBeFalse();
        registry.TryGet("orders.other", 3, out _).ShouldBeFalse();
    }

    [TestMethod]
    public void IntentHashUsesCanonicalPayloadAndNormalizedUtcDueTime()
    {
        DateTimeOffset dueUtc = new DateTimeOffset(2026, 10, 23, 12, 30, 0, TimeSpan.Zero).AddTicks(1230);
        DateTimeOffset equivalentOffset = dueUtc.ToOffset(TimeSpan.FromHours(2));

        ModuleJobIntent.IntentHash("{\"b\":2,\"a\":1}", "schedule", dueUtc)
            .ShouldBe(ModuleJobIntent.IntentHash("{ \"a\": 1, \"b\": 2 }", "schedule", equivalentOffset));
        ModuleJobIntent.IntentHash("{\"a\":1}", "enqueue", null)
            .ShouldNotBe(ModuleJobIntent.IntentHash("{\"a\":1}", "schedule", dueUtc));
        ModuleJobIntent.IntentHash("{\"a\":1}", "schedule", dueUtc)
            .ShouldNotBe(ModuleJobIntent.IntentHash("{\"a\":2}", "schedule", dueUtc));
        ModuleJobIntent.IntentHash("{\"a\":1}", "schedule", dueUtc)
            .ShouldNotBe(ModuleJobIntent.IntentHash("{\"a\":1}", "schedule", dueUtc.AddMicroseconds(1)));
        ModuleJobIntent.NormalizeDue(equivalentOffset).ShouldBe(dueUtc);
    }

    [TestMethod]
    public void RequestIdentityIsStableAndScopedToTheUserActor()
    {
        Guid actorId = Guid.Parse("951d2ee4-b160-4988-903c-c4e6a1d88264");

        ModuleJobIntent.RequestIdentity(actorId).ShouldBe("user/951d2ee4b1604988903cc4e6a1d88264");
    }

    [TestMethod]
    public void CreateWorkPersistsTrustedUserCommandProvenanceAndNormalizedIntent()
    {
        Guid organizationId = Guid.Parse("9a5e24b1-9d4a-4e94-b72a-25e9807c76be");
        Guid actorId = Guid.Parse("951d2ee4-b160-4988-903c-c4e6a1d88264");
        Guid membershipId = Guid.Parse("aa6db1ef-2a6b-44d4-9b9f-bac612d83f8e");
        Guid requestId = Guid.Parse("b7f93fd2-9b48-4b52-a692-4dbbcd52b234");
        DateTimeOffset due = new(2026, 10, 23, 12, 30, 0, TimeSpan.FromHours(2));
        DateTimeOffset now = new(2026, 10, 23, 10, 0, 0, TimeSpan.Zero);
        ModuleJobRegistration registration = Registration();
        ModuleExecutionIdentity identity = new(
            organizationId,
            actorId,
            actorId,
            membershipId,
            ModuleExecutionKind.UserCommand,
            null,
            null,
            null,
            ModuleOperationRegistry.CatalogVersion,
            "trace-123");

        ModuleWorkItem work = ModuleJobIntent.CreateWork(
            registration,
            organizationId,
            identity,
            requestId,
            ModuleJobIntent.RequestIdentity(actorId),
            "{ \"value\": 1 }",
            "schedule",
            due,
            now);

        work.Id.ShouldNotBe(Guid.Empty);
        work.OrganizationId.ShouldBe(organizationId);
        work.ActorId.ShouldBe(actorId);
        work.RequestId.ShouldBe(requestId);
        work.ExecutionIdentityKey.ShouldBe("user/951d2ee4b1604988903cc4e6a1d88264");
        work.JobKey.ShouldBe("orders.reconcile");
        work.JobVersion.ShouldBe(3);
        work.OperationKey.ShouldBe("orders.reconcile-work");
        work.OperationVersion.ShouldBe(2);
        work.ContextVersion.ShouldBe(1);
        work.PayloadHash.ShouldBe(OutboxPayloadHash.Compute("{ \"value\": 1 }"));
        work.IntentHash.ShouldBe(ModuleJobIntent.IntentHash(work.Payload, "schedule", work.DueAtUtc));
        work.SubmissionMode.ShouldBe("schedule");
        work.DueAtUtc.ShouldBe(due.ToUniversalTime());
        work.NextAttemptAtUtc.ShouldBe(due.ToUniversalTime());
        work.State.ShouldBe("queued");
        work.Attempts.ShouldBe(0);
        work.CreatedAtUtc.ShouldBe(now);

        using JsonDocument envelope = JsonDocument.Parse(work.ExecutionContextJson);
        JsonElement root = envelope.RootElement;
        root.GetProperty("version").GetInt32().ShouldBe(1);
        root.GetProperty("moduleId").GetString().ShouldBe("orders");
        JsonElement persistedIdentity = root.GetProperty("identity");
        persistedIdentity.GetProperty("organizationId").GetGuid().ShouldBe(organizationId);
        persistedIdentity.GetProperty("effectiveActorId").GetGuid().ShouldBe(actorId);
        persistedIdentity.GetProperty("membershipId").GetGuid().ShouldBe(membershipId);
        persistedIdentity.GetProperty("executionKind").GetInt32().ShouldBe((int)ModuleExecutionKind.UserCommand);
        persistedIdentity.GetProperty("grantKey").ValueKind.ShouldBe(JsonValueKind.Null);
        persistedIdentity.GetProperty("grantVersion").ValueKind.ShouldBe(JsonValueKind.Null);
        persistedIdentity.GetProperty("sourceMessageId").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [TestMethod]
    public void EnabledJobsRejectAnOwnerConnectionEvenOutsideProduction()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jobs:Enabled"] = "true",
            ["ConnectionStrings:trykatch-work"] = "Host=localhost;Database=jobs;Username=postgres"
        }).Build();
        Should.Throw<InvalidOperationException>(() => new ServiceCollection().AddModuleJobs(configuration));
    }

    private static ModuleJobRegistration Registration()
    {
        ModuleOperationDescriptor operation = new(
            "orders",
            "orders.reconcile-work",
            2,
            ModuleExecutionKind.UserCommand,
            "orders.reconcile",
            1,
            [],
            "orders.write",
            null,
            2048);
        ModuleJobDescriptor job = new(
            "orders",
            "orders.reconcile",
            3,
            operation.OperationKey,
            operation.OperationVersion,
            operation.ExecutionKind,
            operation.PayloadSchemaKey,
            operation.PayloadSchemaVersion,
            operation.MaximumPayloadBytes);

        return new ModuleJobRegistration(job, new ModuleOperationRegistration(operation, null, null!, null));
    }

    private sealed class FakeHandler : IModuleOperationHandler
    {
        public string ModuleId => "orders";
        public string OperationKey => "orders.reconcile-work";
        public int OperationVersion => 2;
        public Task HandleAsync(ModuleOperationContext context, JsonElement payload, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakePolicy : IModuleOperationResourcePolicy
    {
        public string ModuleId => "orders";
        public string PolicyKey => "orders.write";
        public Task<bool> AuthorizeAsync(ModuleOperationContext context, JsonElement payload, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class StubModule(ModuleDescriptor descriptor) : IModule
    {
        public ModuleDescriptor Descriptor { get; } = descriptor;
        public void Register(IServiceCollection services, IConfiguration configuration) { }
    }
}
