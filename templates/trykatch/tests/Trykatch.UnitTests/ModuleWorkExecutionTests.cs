using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trykatch.Modules;
using Trykatch.Infrastructure.Execution;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class ModuleWorkExecutionTests
{
    private const string ModuleId = "orders";
    private const string OperationKey = "orders.reconcile";
    private const string ResourceKey = "orders.read";
    private const string GrantKey = "orders.maintenance";
    private static readonly Guid AuditActorId = Guid.Parse("bdbab2a4-e7dc-4e54-b174-60879511d255");
    private static readonly Guid WorkOrganizationId = Guid.Parse("11f64dab-e42d-4aa4-9400-855a96a1a4d2");
    private static readonly Guid OriginActorId = Guid.Parse("d197faad-7633-430e-a47b-090b79b0c61c");
    private static readonly Guid SourceMessageId = Guid.Parse("2117a774-7063-42c4-ab86-b9fb9648d248");

    [TestMethod]
    public void RegistryResolvesOneExplicitUserHandlerAndResourcePolicy()
    {
        ModuleCatalog workCatalog = Catalog(UserModule());
        FakeHandler handler = new(ModuleId, OperationKey, 1);
        FakePolicy policy = new(ModuleId, ResourceKey);

        ModuleOperationRegistry registry = new(workCatalog, [handler], [policy]);

        ModuleOperationRegistry.CatalogVersion.ShouldBe(1);
        registry.ContainsModule(ModuleId).ShouldBeTrue();
        registry.TryGet(ModuleId, OperationKey, 1, out ModuleOperationRegistration? registration).ShouldBeTrue();
        registration!.Handler.ShouldBe(handler);
        registration.ResourcePolicy.ShouldBe(policy);
        registry.TryGet(ModuleId, OperationKey, 2, out _).ShouldBeFalse();
        registry.ContainsModule("not-installed").ShouldBeFalse();
    }

    [TestMethod]
    public void RegistryRejectsDuplicateAndMissingHandlers()
    {
        ModuleCatalog workCatalog = Catalog(UserModule());
        FakePolicy policy = new(ModuleId, ResourceKey);

        Should.Throw<InvalidOperationException>(() => new ModuleOperationRegistry(
            workCatalog,
            [new FakeHandler(ModuleId, OperationKey, 1), new FakeHandler(ModuleId, OperationKey, 1)],
            [policy]));
        Should.Throw<InvalidOperationException>(() => new ModuleOperationRegistry(workCatalog, [], [policy]));
    }

    [TestMethod]
    public void RegistryRejectsDuplicateAndMissingResourcePolicies()
    {
        ModuleCatalog workCatalog = Catalog(UserModule());
        FakeHandler handler = new(ModuleId, OperationKey, 1);

        Should.Throw<InvalidOperationException>(() => new ModuleOperationRegistry(
            workCatalog,
            [handler],
            [new FakePolicy(ModuleId, ResourceKey), new FakePolicy(ModuleId, ResourceKey)]));
        Should.Throw<InvalidOperationException>(() => new ModuleOperationRegistry(workCatalog, [handler], []));
    }

    [TestMethod]
    public void SystemGrantCannotRunAnotherOperation()
    {
        ModuleOperationRegistration registration = Registration(ScheduledModule());
        ModuleExecutionIdentity identity = SystemIdentity();

        SystemWorkGrantValidator.TryValidate(registration, identity).ShouldBeTrue();
        SystemWorkGrantValidator.TryValidate(registration, identity with { GrantKey = "orders.other" }).ShouldBeFalse();
    }

    [TestMethod]
    public void SystemGrantRejectsWrongVersionActorKindOrMembership()
    {
        ModuleOperationRegistration registration = Registration(ScheduledModule());
        ModuleExecutionIdentity identity = SystemIdentity();

        SystemWorkGrantValidator.TryValidate(registration, identity with { GrantVersion = 2 }).ShouldBeFalse();
        SystemWorkGrantValidator.TryValidate(registration, identity with { EffectiveActorId = OriginActorId }).ShouldBeFalse();
        SystemWorkGrantValidator.TryValidate(registration, identity with { ExecutionKind = ModuleExecutionKind.UserCommand }).ShouldBeFalse();
        SystemWorkGrantValidator.TryValidate(registration, identity with { MembershipId = OriginActorId }).ShouldBeFalse();
        SystemWorkGrantValidator.TryValidate(registration, identity with { SourceMessageId = ModuleWorkExecutionTests.SourceMessageId }).ShouldBeFalse();
    }

    [TestMethod]
    public void CommittedEventGrantRequiresSourceMessageAndRegisteredMatchingConsumer()
    {
        ModuleDescriptor producer = Module("events") with
        {
            IntegrationEvents = [new IntegrationEventDescriptor("events.committed", 1, "events", typeof(JsonElement), element => element, 1024)]
        };
        ModuleDescriptor consumer = SystemModule(ModuleId, ModuleExecutionKind.CommittedEvent, sourceEvent: true) with
        {
            Requires = ["events"]
        };
        ModuleCatalog workCatalog = Catalog(producer, consumer);
        ModuleOperationRegistry registry = new(
            workCatalog,
            [new FakeHandler(ModuleId, OperationKey, 1)],
            [new FakePolicy(ModuleId, ResourceKey)]);
        registry.TryGet(ModuleId, OperationKey, 1, out ModuleOperationRegistration? registration).ShouldBeTrue();
        ModuleExecutionIdentity identity = SystemIdentity(ModuleExecutionKind.CommittedEvent, SourceMessageId);

        SystemWorkGrantValidator.TryValidate(registration!, identity with { SourceMessageId = null }).ShouldBeFalse();
        SystemWorkGrantValidator.TryValidate(registration!, identity).ShouldBeTrue();

        registry.AcceptsSourceEvent(registration!, "events.committed", 1).ShouldBeTrue();
        registry.AcceptsSourceEvent(registration!, "events.committed", 2).ShouldBeFalse();
        registry.AcceptsSourceEvent(registration!, "events.unknown", 1).ShouldBeFalse();
    }

    private static ModuleCatalog Catalog(params ModuleDescriptor[] descriptors) =>
        new(descriptors.Select(descriptor => new StubModule(descriptor)));

    private static ModuleOperationRegistration Registration(ModuleDescriptor descriptor) =>
        Registration(Catalog(descriptor), descriptor.Id, OperationKey, 1);

    private static ModuleOperationRegistration Registration(ModuleCatalog catalog, string moduleId, string operationKey, int version)
    {
        ModuleOperationRegistry registry = new(catalog, [new FakeHandler(moduleId, operationKey, version)], [new FakePolicy(moduleId, ResourceKey)]);
        registry.TryGet(moduleId, operationKey, version, out ModuleOperationRegistration? registration).ShouldBeTrue();
        return registration!;
    }

    private static ModuleExecutionIdentity SystemIdentity(
        ModuleExecutionKind kind = ModuleExecutionKind.ScheduledSystem,
        Guid? sourceMessageId = null) =>
        new(WorkOrganizationId, AuditActorId, OriginActorId, null, kind, GrantKey, 1, sourceMessageId, 1, null);

    private static ModuleDescriptor UserModule() => Module(ModuleId) with
    {
        Capabilities = ModuleCapabilities.Api | ModuleCapabilities.BackgroundWork | ModuleCapabilities.Data,
        DefaultDataOwnership = ModuleDataOwnership.Organization,
        Operations = [Operation(ModuleId, OperationKey, ModuleExecutionKind.UserCommand, systemGrantKey: null)],
        DataResources = [Resource(ModuleId, ResourceKey)]
    };

    private static ModuleDescriptor ScheduledModule() => SystemModule(ModuleId, ModuleExecutionKind.ScheduledSystem);

    private static ModuleDescriptor SystemModule(string moduleId, ModuleExecutionKind kind, bool sourceEvent = false) => Module(moduleId) with
    {
        Capabilities = ModuleCapabilities.Api | ModuleCapabilities.BackgroundWork | ModuleCapabilities.Data,
        DefaultDataOwnership = ModuleDataOwnership.Organization,
        Operations = [Operation(moduleId, OperationKey, kind, GrantKey)],
        SystemGrants = [new ModuleSystemGrantDescriptor(GrantKey, 1, moduleId, AuditActorId, [OperationKey], [ResourceKey])],
        DataResources = [Resource(moduleId, ResourceKey)],
        EventConsumers = sourceEvent
            ? [new ModuleEventConsumerDescriptor("orders.committed-consumer", moduleId, [new ModuleEventVersion("events.committed", 1)], OperationKey, 1, GrantKey)]
            : []
    };

    private static ModuleDescriptor Module(string moduleId) =>
        new(moduleId, moduleId, "1.0.0", $"{moduleId} execution tests", [], [], ModuleCapabilities.Api | ModuleCapabilities.BackgroundWork, []);

    private static ModuleOperationDescriptor Operation(string moduleId, string operationKey, ModuleExecutionKind kind, string? systemGrantKey) =>
        new(moduleId, operationKey, 1, kind, $"{operationKey}.payload", 1, [], ResourceKey, systemGrantKey, 1024);

    private static DataResourceDescriptor Resource(string moduleId, string resourceKey) =>
        new(resourceKey, "app", $"{moduleId.Replace('-', '_')}_execution", ModuleDataOwnership.Organization, IsolationPolicy: $"{moduleId}-rls");

    private sealed class FakeHandler(string moduleId, string operationKey, int operationVersion) : IModuleOperationHandler
    {
        public string ModuleId { get; } = moduleId;
        public string OperationKey { get; } = operationKey;
        public int OperationVersion { get; } = operationVersion;

        public Task HandleAsync(ModuleOperationContext context, JsonElement payload, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakePolicy(string moduleId, string policyKey) : IModuleOperationResourcePolicy
    {
        public string ModuleId { get; } = moduleId;
        public string PolicyKey { get; } = policyKey;

        public Task<bool> AuthorizeAsync(ModuleOperationContext context, JsonElement payload, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class StubModule(ModuleDescriptor descriptor) : IModule
    {
        public ModuleDescriptor Descriptor { get; } = descriptor;
        public void Register(IServiceCollection services, IConfiguration configuration) { }
    }
}
