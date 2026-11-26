using System.Text.Json;
using Trykatch.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class ModuleExecutionContractTests
{
    private const int PayloadLimit = 1024 * 1024;

    [TestMethod]
    public void RegistryRejectsDuplicateConsumerKeysAndUnknownWorkKinds()
    {
        ModuleDescriptor first = Module("orders") with
        {
            Capabilities = ModuleCapabilities.Api | ModuleCapabilities.BackgroundWork | ModuleCapabilities.Data,
            DefaultDataOwnership = ModuleDataOwnership.Organization,
            IntegrationEvents = [OrderCommittedEvent()],
            Operations = [Operation("orders", "orders.reconcile", ModuleExecutionKind.CommittedEvent, resourcePolicyKey: "orders.read")],
            SystemGrants = [Grant("orders", "orders.reconcile", "orders.read")],
            DataResources = [Resource("orders", "orders.read")],
            EventConsumers = [Consumer("orders", "orders.reconcile", "orders.committed", "orders.reconcile")]
        };
        ModuleDescriptor second = Module("reporting", requires: ["orders"]) with
        {
            Capabilities = ModuleCapabilities.Api | ModuleCapabilities.BackgroundWork | ModuleCapabilities.Data,
            DefaultDataOwnership = ModuleDataOwnership.Organization,
            Operations = [Operation("reporting", "reporting.refresh", ModuleExecutionKind.CommittedEvent, resourcePolicyKey: "reporting.read")],
            SystemGrants = [Grant("reporting", "reporting.refresh", "reporting.read")],
            DataResources = [Resource("reporting", "reporting.read")],
            EventConsumers = [Consumer("reporting", "orders.reconcile", "orders.committed", "reporting.refresh")]
        };

        Should.Throw<InvalidOperationException>(() => new ModuleCatalog([
            new StubModule(first),
            new StubModule(second)
        ])).Message.ShouldContain("consumer");

        ModuleDescriptor unknownKind = Module("orders") with
        {
            Operations = [Operation("orders", "orders.reconcile", (ModuleExecutionKind)999)]
        };
        Should.Throw<InvalidOperationException>(() => new ModuleCatalog([new StubModule(unknownKind)]));
    }

    [TestMethod]
    public void CatalogRejectsGrantThatReferencesAnUnregisteredOperationOrResource()
    {
        ModuleDescriptor descriptor = Module("orders") with
        {
            Operations = [Operation("orders", "orders.reconcile", ModuleExecutionKind.ScheduledSystem)],
            SystemGrants =
            [
                new ModuleSystemGrantDescriptor(
                    "orders.maintenance",
                    1,
                    "orders",
                    Guid.NewGuid(),
                    ["orders.missing"],
                    ["orders.missing-resource"])
            ]
        };

        Should.Throw<InvalidOperationException>(() => new ModuleCatalog([new StubModule(descriptor)]));
    }

    [TestMethod]
    public void CatalogRejectsInvalidVersionsAndForeignDescriptorOwnership()
    {
        ModuleDescriptor badVersion = Module("orders") with
        {
            Operations = [Operation("orders", "orders.reconcile", ModuleExecutionKind.UserCommand, version: 0)]
        };
        Should.Throw<InvalidOperationException>(() => new ModuleCatalog([new StubModule(badVersion)]));

        ModuleDescriptor foreignOwner = Module("orders") with
        {
            Operations = [Operation("reporting", "orders.reconcile", ModuleExecutionKind.UserCommand)]
        };
        Should.Throw<InvalidOperationException>(() => new ModuleCatalog([new StubModule(foreignOwner)]));
    }

    [TestMethod]
    public void CatalogRejectsUnboundedPayloadAndUnregisteredRequiredPermission()
    {
        ModuleDescriptor tooLarge = Module("orders") with
        {
            Operations = [Operation("orders", "orders.reconcile", ModuleExecutionKind.UserCommand, maximumPayloadBytes: PayloadLimit + 1)]
        };
        Should.Throw<InvalidOperationException>(() => new ModuleCatalog([new StubModule(tooLarge)]));

        ModuleDescriptor missingPermission = Module("orders") with
        {
            Operations = [Operation(
                "orders",
                "orders.reconcile",
                ModuleExecutionKind.UserCommand,
                permissions: ["orders.manage"])]
        };
        Should.Throw<InvalidOperationException>(() => new ModuleCatalog([new StubModule(missingPermission)]));
    }

    [TestMethod]
    public void CatalogAcceptsTwoConsumersOfOneExplicitlyOwnedEvent()
    {
        ModuleDescriptor producer = Module("orders") with
        {
            IntegrationEvents = [OrderCommittedEvent()],
            Capabilities = ModuleCapabilities.Api | ModuleCapabilities.BackgroundWork | ModuleCapabilities.Data,
            DefaultDataOwnership = ModuleDataOwnership.Organization,
            Operations = [Operation("orders", "orders.reconcile", ModuleExecutionKind.CommittedEvent, resourcePolicyKey: "orders.read")],
            SystemGrants = [Grant("orders", "orders.reconcile", "orders.read")],
            DataResources = [Resource("orders", "orders.read")]
        };
        ModuleDescriptor inventory = Module("inventory", requires: ["orders"]) with
        {
            Capabilities = ModuleCapabilities.Api | ModuleCapabilities.BackgroundWork | ModuleCapabilities.Data,
            DefaultDataOwnership = ModuleDataOwnership.Organization,
            Operations = [Operation("inventory", "inventory.reserve", ModuleExecutionKind.CommittedEvent, resourcePolicyKey: "inventory.stock")],
            SystemGrants = [Grant("inventory", "inventory.reserve", "inventory.stock")],
            DataResources = [Resource("inventory", "inventory.stock")],
            EventConsumers = [Consumer("inventory", "inventory.reserve-order", "orders.committed", "inventory.reserve")]
        };
        ModuleDescriptor analytics = Module("analytics", optionalDependencies: ["orders"]) with
        {
            Capabilities = ModuleCapabilities.Api | ModuleCapabilities.BackgroundWork | ModuleCapabilities.Data,
            DefaultDataOwnership = ModuleDataOwnership.Organization,
            Operations = [Operation("analytics", "analytics.index", ModuleExecutionKind.CommittedEvent, resourcePolicyKey: "analytics.index")],
            SystemGrants = [Grant("analytics", "analytics.index", "analytics.index")],
            DataResources = [Resource("analytics", "analytics.index")],
            EventConsumers = [Consumer("analytics", "analytics.index-order", "orders.committed", "analytics.index")]
        };

        ModuleCatalog catalog = new([
            new StubModule(inventory),
            new StubModule(analytics),
            new StubModule(producer)
        ]);

        catalog.Descriptors.SelectMany(module => module.EventConsumers).Count().ShouldBe(2);
        catalog.Descriptors.Select(module => module.Id).ShouldBe(["orders", "analytics", "inventory"]);
    }

    [TestMethod]
    public void CatalogRejectsConsumerWithoutDeclaredDependencyOnEventOwner()
    {
        ModuleDescriptor producer = Module("orders") with { IntegrationEvents = [OrderCommittedEvent()] };
        ModuleDescriptor consumer = Module("inventory") with
        {
            Capabilities = ModuleCapabilities.Api | ModuleCapabilities.BackgroundWork | ModuleCapabilities.Data,
            DefaultDataOwnership = ModuleDataOwnership.Organization,
            Operations = [Operation("inventory", "inventory.reserve", ModuleExecutionKind.CommittedEvent, resourcePolicyKey: "inventory.stock")],
            SystemGrants = [Grant("inventory", "inventory.reserve", "inventory.stock")],
            DataResources = [Resource("inventory", "inventory.stock")],
            EventConsumers = [Consumer("inventory", "inventory.reserve-order", "orders.committed", "inventory.reserve")]
        };

        Should.Throw<InvalidOperationException>(() => new ModuleCatalog([
            new StubModule(producer),
            new StubModule(consumer)
        ])).Message.ShouldContain("depend");
    }

    [TestMethod]
    public void CatalogRejectsConsumerWhenOptionalEventOwnerIsNotInstalled()
    {
        ModuleDescriptor consumer = Module("inventory", optionalDependencies: ["orders"]) with
        {
            Capabilities = ModuleCapabilities.Api | ModuleCapabilities.BackgroundWork | ModuleCapabilities.Data,
            DefaultDataOwnership = ModuleDataOwnership.Organization,
            Operations = [Operation("inventory", "inventory.reserve", ModuleExecutionKind.CommittedEvent, resourcePolicyKey: "inventory.stock")],
            SystemGrants = [Grant("inventory", "inventory.reserve", "inventory.stock")],
            DataResources = [Resource("inventory", "inventory.stock")],
            EventConsumers = [Consumer("inventory", "inventory.reserve-order", "orders.committed", "inventory.reserve")]
        };

        Should.Throw<InvalidOperationException>(() => new ModuleCatalog([new StubModule(consumer)]));
    }

    [TestMethod]
    public void AddModulesValidatesExecutionDescriptorsBeforeModuleActivation()
    {
        StubModule invalid = new(Module("orders") with
        {
            Operations = [Operation("orders", "orders.reconcile", (ModuleExecutionKind)999)]
        });
        ServiceCollection services = new();
        IConfiguration configuration = new ConfigurationBuilder().Build();

        Should.Throw<InvalidOperationException>(() => services.AddModules(configuration, [invalid]));

        invalid.RegisterWasCalled.ShouldBeFalse();
    }

    private static ModuleDescriptor Module(string id, IReadOnlyList<string>? requires = null, IReadOnlyList<string>? optionalDependencies = null) =>
        new(id, id, "1.0.0", $"{id} contract tests", requires ?? [], optionalDependencies ?? [], ModuleCapabilities.Api | ModuleCapabilities.BackgroundWork, []);

    private static ModuleOperationDescriptor Operation(
        string owner,
        string key,
        ModuleExecutionKind kind,
        int version = 1,
        IReadOnlyList<string>? permissions = null,
        int maximumPayloadBytes = PayloadLimit,
        string? resourcePolicyKey = null) =>
        new(owner, key, version, kind, $"{key}.payload", 1, permissions ?? [], resourcePolicyKey ?? $"{owner}.read", kind == ModuleExecutionKind.UserCommand ? null : $"{owner}.maintenance", maximumPayloadBytes);

    private static ModuleSystemGrantDescriptor Grant(string owner, string operation, string resource) =>
        new($"{owner}.maintenance", 1, owner, Guid.NewGuid(), [operation], [resource]);

    private static IntegrationEventDescriptor OrderCommittedEvent() =>
        new("orders.committed", 1, "orders", typeof(OrderPayload), element => JsonSerializer.Deserialize<OrderPayload>(element)!, PayloadLimit);

    private static ModuleEventConsumerDescriptor Consumer(
        string owner,
        string key,
        string eventKey,
        string operation) =>
        new(key, owner, [new ModuleEventVersion(eventKey, 1)], operation, 1, $"{owner}.maintenance");

    private static DataResourceDescriptor Resource(string owner, string name) =>
        new(name, "app", name.Replace('.', '_'), ModuleDataOwnership.Organization, IsolationPolicy: $"{owner}-rls");

    private sealed record OrderPayload(string Id);

    private sealed class StubModule(ModuleDescriptor descriptor) : IModule
    {
        public ModuleDescriptor Descriptor { get; } = descriptor;
        public bool RegisterWasCalled { get; private set; }

        public void Register(IServiceCollection services, IConfiguration configuration) => RegisterWasCalled = true;
    }
}
