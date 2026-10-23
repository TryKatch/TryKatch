using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trykatch.Application.Outbox;
using Trykatch.Infrastructure.Eventing;
using Trykatch.Infrastructure.Execution;
using Trykatch.Modules;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class ModuleEventDeliveryTests
{
    private static readonly Guid AuditActorId = Guid.Parse("1c629a78-58c2-4a77-8b92-e42a46f20281");
    private static readonly Guid OriginActorId = Guid.Parse("e68a40d7-9b54-437a-8614-b19c6a5d31df");
    private static readonly Guid EffectiveActorId = Guid.Parse("b9af5377-4ed0-48d5-a381-5a8f1e07f35b");
    private const string EventKey = "orders.committed";
    private const string ConsumerKey = "inventory.reserve-order";
    private const string OperationKey = "inventory.reserve";
    private const string GrantKey = "inventory.event-effects";

    [TestMethod]
    public void RegistryRequiresExactlyOneExplicitBindingForEveryInstalledConsumer()
    {
        ModuleCatalog catalog = Catalog();
        TestConsumer consumer = new();

        Should.Throw<InvalidOperationException>(() => new ModuleEventRegistry(catalog, []))
            .Message.ShouldContain("Missing");
        Should.Throw<InvalidOperationException>(() => new ModuleEventRegistry(catalog,
            [Binding(consumer), Binding(new TestConsumer())]))
            .Message.ShouldContain("Duplicate");
        Should.Throw<InvalidOperationException>(() => new ModuleEventRegistry(catalog,
            [new ModuleEventConsumerBinding("inventory", "unknown.consumer", consumer)]))
            .Message.ShouldContain("matching installed consumer");

        ModuleEventRegistry registry = new(catalog, [Binding(consumer)]);
        registry.TryGet(ConsumerKey, out ModuleEventConsumerRegistration? registration).ShouldBeTrue();
        registration!.Descriptor.ConsumerKey.ShouldBe(ConsumerKey);
        registration.Operation.OperationKey.ShouldBe(OperationKey);
        registration.Grant.GrantKey.ShouldBe(GrantKey);
        registration.Consumer.ShouldBe(consumer);
        registry.TryGet("unknown.consumer", out _).ShouldBeFalse();
    }

    [TestMethod]
    public void RegistryResolvesCompatibleConsumersAndExplicitEventVersions()
    {
        ModuleEventRegistry registry = new(Catalog(), [Binding(new TestConsumer())]);

        registry.ConsumersFor(EventKey, 1).Select(item => item.Descriptor.ConsumerKey).ShouldBe([ConsumerKey]);
        registry.ConsumersFor(EventKey, 2).ShouldBeEmpty();
        registry.TryGetEvent(EventKey, 1, out IntegrationEventDescriptor? descriptor).ShouldBeTrue();
        descriptor!.OwnerModuleId.ShouldBe("orders");
        registry.TryGetEvent(EventKey, 2, out _).ShouldBeFalse();
        registry.TryGetSourceOperation("orders.create", 1, out ModuleOperationDescriptor? source).ShouldBeTrue();
        source!.ExecutionKind.ShouldBe(ModuleExecutionKind.UserCommand);
        registry.TryGetSourceOperation("orders.missing", 1, out _).ShouldBeFalse();
    }

    [TestMethod]
    public void UserSourceRequiresCurrentRegisteredUserOperationAndTrustedOriginMembership()
    {
        ModuleEventRegistry registry = new(Catalog(), [Binding(new TestConsumer())]);
        OutboxContext source = UserSource();

        registry.TryValidateSource(source, "{\"OrderId\":\"123\"}", out IntegrationEventDescriptor? descriptor, out string? failure).ShouldBeTrue();
        descriptor!.EventKey.ShouldBe(EventKey);
        failure.ShouldBeNull();
        registry.TryValidateSource(source with { MembershipId = null }, "{}", out _, out failure).ShouldBeFalse();
        failure.ShouldBe("source_event_invalid");
        registry.TryValidateSource(source with { OriginActorId = OriginActorId }, "{}", out _, out _).ShouldBeFalse();
        registry.TryValidateSource(source with { ExecutionKind = "ScheduledSystem" }, "{}", out _, out _).ShouldBeFalse();
        registry.TryValidateSource(source with { OperationKey = "orders.missing" }, "{}", out _, out _).ShouldBeFalse();
        registry.TryValidateSource(source with { CatalogVersion = "different-catalog-fingerprint" }, "{}", out _, out _).ShouldBeTrue();
    }

    [TestMethod]
    public void SystemSourceRequiresRegisteredGrantActorWithoutMembership()
    {
        ModuleEventRegistry registry = new(Catalog(), [Binding(new TestConsumer())]);
        OutboxContext source = SystemSource();

        registry.TryValidateSource(source, "{\"OrderId\":\"123\"}", out _, out _).ShouldBeTrue();
        registry.TryValidateSource(source with { MembershipId = Guid.NewGuid() }, "{}", out _, out _).ShouldBeFalse();
        registry.TryValidateSource(source with { OperationKey = "inventory.reserve" }, "{}", out _, out _).ShouldBeFalse();
        registry.TryValidateSource(source with { OperationVersion = 2 }, "{}", out _, out _).ShouldBeFalse();
        registry.TryValidateSource(source with { EffectiveActorId = OriginActorId }, "{}", out _, out _).ShouldBeFalse();
        registry.TryValidateSource(source with { ExecutionKind = "ScheduledSystem" }, "{}", out _, out _).ShouldBeFalse();
        registry.TryValidateSource(source with { CatalogVersion = "different-catalog-fingerprint" }, "{}", out _, out _).ShouldBeTrue();
    }

    [TestMethod]
    public void ScheduledSystemSourceRequiresMatchingRegisteredSystemOperationAndGrant()
    {
        ModuleEventRegistry registry = new(Catalog(), [Binding(new TestConsumer())]);
        OutboxContext source = ScheduledSystemSource();

        registry.TryValidateSource(source, "{\"OrderId\":\"123\"}", out _, out _).ShouldBeTrue();
        registry.TryValidateSource(source with { ExecutionKind = "CommittedEvent" }, "{}", out _, out _).ShouldBeFalse();
        registry.TryValidateSource(source with { OperationKey = "orders.reconcile" }, "{}", out _, out _).ShouldBeFalse();
        registry.TryValidateSource(source with { EffectiveActorId = OriginActorId }, "{}", out _, out _).ShouldBeFalse();
        registry.TryValidateSource(source with { MembershipId = Guid.NewGuid() }, "{}", out _, out _).ShouldBeFalse();
    }

    [TestMethod]
    public void EventReaderRejectsUnknownVersionsMalformedJsonAndWrongReaderTypesWithFixedCode()
    {
        ModuleEventRegistry registry = new(Catalog(), [Binding(new TestConsumer())]);
        OutboxContext source = UserSource();

        registry.TryValidateSource(source with { EventKey = "orders.unknown" }, "{}", out _, out string? failure).ShouldBeFalse();
        failure.ShouldBe("source_event_invalid");
        registry.TryValidateSource(source with { SchemaVersion = 2 }, "{}", out _, out failure).ShouldBeFalse();
        failure.ShouldBe("source_event_invalid");
        registry.TryValidateSource(source, "{", out _, out failure).ShouldBeFalse();
        failure.ShouldBe("source_event_invalid");

        ModuleCatalog wrongReaderCatalog = Catalog(reader: _ => "wrong type");
        ModuleEventRegistry wrongReaderRegistry = new(wrongReaderCatalog, [Binding(new TestConsumer())]);
        wrongReaderRegistry.TryValidateSource(source, "{}", out _, out failure).ShouldBeFalse();
        failure.ShouldBe("source_event_invalid");

        ModuleEventRegistry throwingReaderRegistry = new(Catalog(_ => throw new InvalidOperationException("private-reader-diagnostic")), [Binding(new TestConsumer())]);
        throwingReaderRegistry.TryValidateSource(source, "{}", out _, out failure).ShouldBeFalse();
        failure.ShouldBe("source_event_invalid");
    }

    [TestMethod]
    public void OperationRegistryAllowsNullHandlerOnlyForExplicitConsumerOperation()
    {
        ModuleCatalog catalog = OperationRegistryCatalog();
        TestConsumer consumer = new();
        ModuleEventConsumerBinding binding = Binding(consumer);
        FakePolicy policy = new("inventory", "inventory.stock");

        ModuleOperationRegistry registry = new(catalog, [], [policy], [binding]);
        registry.TryGet("inventory", OperationKey, 1, out ModuleOperationRegistration? registration).ShouldBeTrue();
        registration!.Handler.ShouldBeNull();
        registration.ResourcePolicy.ShouldBe(policy);

        Should.Throw<InvalidOperationException>(() => new ModuleOperationRegistry(catalog, [], [policy]));
        ModuleOperationRegistry withHandler = new(catalog, [new FakeHandler("inventory", OperationKey, 1)], [policy], [binding]);
        withHandler.TryGet("inventory", OperationKey, 1, out ModuleOperationRegistration? withHandlerRegistration).ShouldBeTrue();
        withHandlerRegistration!.Handler.ShouldBeOfType<FakeHandler>();
        Should.Throw<InvalidOperationException>(() => new ModuleOperationRegistry(catalog,
            [], [policy], [new ModuleEventConsumerBinding("inventory", "inventory.other", consumer)]));
    }

    private static ModuleCatalog OperationRegistryCatalog()
    {
        ModuleDescriptor inventory = Module("inventory") with
        {
            Capabilities = ModuleCapabilities.Api | ModuleCapabilities.BackgroundWork | ModuleCapabilities.Data,
            DefaultDataOwnership = ModuleDataOwnership.Organization,
            IntegrationEvents = [new IntegrationEventDescriptor(EventKey, 1, "inventory", typeof(OrderPayload), element => JsonSerializer.Deserialize<OrderPayload>(element)!, 4096)],
            Operations = [new("inventory", OperationKey, 1, ModuleExecutionKind.CommittedEvent, "inventory.reserve.payload", 1, [], "inventory.stock", GrantKey, 4096)],
            SystemGrants = [new(GrantKey, 2, "inventory", AuditActorId, [OperationKey], ["inventory.stock"])],
            DataResources = [Resource("inventory", "inventory.stock")],
            EventConsumers = [new(ConsumerKey, "inventory", [new(EventKey, 1)], OperationKey, 1, GrantKey)]
        };
        return new ModuleCatalog([new StubModule(inventory)]);
    }

    private static ModuleCatalog Catalog(Func<JsonElement, object>? reader = null)
    {
        ModuleDescriptor orders = Module("orders") with
        {
            Capabilities = ModuleCapabilities.Api | ModuleCapabilities.BackgroundWork | ModuleCapabilities.Data,
            DefaultDataOwnership = ModuleDataOwnership.Organization,
            IntegrationEvents = [new IntegrationEventDescriptor(EventKey, 1, "orders", typeof(OrderPayload), reader ?? (element => JsonSerializer.Deserialize<OrderPayload>(element)!), 4096)],
            Operations = [
                new("orders", "orders.create", 1, ModuleExecutionKind.UserCommand, "orders.create.payload", 1, [], "orders.read", null, 4096),
                new("orders", "orders.reconcile", 1, ModuleExecutionKind.CommittedEvent, "orders.reconcile.payload", 1, [], "orders.read", "orders.source-effects", 4096),
                new("orders", "orders.scheduled-maintenance", 1, ModuleExecutionKind.ScheduledSystem, "orders.maintenance.payload", 1, [], "orders.read", "orders.maintenance-effects", 4096)
            ],
            SystemGrants = [
                new("orders.source-effects", 3, "orders", AuditActorId, ["orders.reconcile"], ["orders.read"]),
                new("orders.maintenance-effects", 1, "orders", AuditActorId, ["orders.scheduled-maintenance"], ["orders.read"])
            ],
            DataResources = [Resource("orders", "orders.read")]
        };
        ModuleDescriptor inventory = Module("inventory", requires: ["orders"]) with
        {
            Capabilities = ModuleCapabilities.Api | ModuleCapabilities.BackgroundWork | ModuleCapabilities.Data,
            DefaultDataOwnership = ModuleDataOwnership.Organization,
            Operations = [new("inventory", OperationKey, 1, ModuleExecutionKind.CommittedEvent, "inventory.reserve.payload", 1, [], "inventory.stock", GrantKey, 4096)],
            SystemGrants = [new(GrantKey, 2, "inventory", AuditActorId, [OperationKey], ["inventory.stock"])],
            DataResources = [Resource("inventory", "inventory.stock")],
            EventConsumers = [new(ConsumerKey, "inventory", [new(EventKey, 1)], OperationKey, 1, GrantKey)]
        };
        return new ModuleCatalog([new StubModule(orders), new StubModule(inventory)]);
    }

    private static OutboxContext UserSource() => new(Guid.NewGuid(), EffectiveActorId, EffectiveActorId, Guid.NewGuid(),
        "UserCommand", "orders.create", 1, EventKey, 1, "old-catalog-fingerprint", null);

    private static OutboxContext SystemSource() => new(Guid.NewGuid(), AuditActorId, OriginActorId, null,
        "CommittedEvent", "orders.reconcile", 1, EventKey, 1, "old-catalog-fingerprint", null);

    private static OutboxContext ScheduledSystemSource() => new(Guid.NewGuid(), AuditActorId, OriginActorId, null,
        "ScheduledSystem", "orders.scheduled-maintenance", 1, EventKey, 1, "old-catalog-fingerprint", null);

    private static ModuleEventConsumerBinding Binding(IModuleEventConsumer consumer) => new("inventory", ConsumerKey, consumer);

    private static ModuleDescriptor Module(string id, IReadOnlyList<string>? requires = null) =>
        new(id, id, "1.0.0", $"{id} tests", requires ?? [], [], ModuleCapabilities.Api | ModuleCapabilities.BackgroundWork, []);

    private static DataResourceDescriptor Resource(string moduleId, string key) =>
        new(key, "app", key.Replace('.', '_'), ModuleDataOwnership.Organization, IsolationPolicy: $"{moduleId}-rls");

    private sealed record OrderPayload(string OrderId);

    private sealed class StubModule(ModuleDescriptor descriptor) : IModule
    {
        public ModuleDescriptor Descriptor { get; } = descriptor;
        public void Register(IServiceCollection services, IConfiguration configuration) { }
    }

    private sealed class TestConsumer : IModuleEventConsumer
    {
        public Task HandleAsync(ModuleEventContext context, JsonElement payload, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakePolicy(string moduleId, string policyKey) : IModuleOperationResourcePolicy
    {
        public string ModuleId { get; } = moduleId;
        public string PolicyKey { get; } = policyKey;
        public Task<bool> AuthorizeAsync(ModuleOperationContext context, JsonElement payload, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class FakeHandler(string moduleId, string operationKey, int operationVersion) : IModuleOperationHandler
    {
        public string ModuleId { get; } = moduleId;
        public string OperationKey { get; } = operationKey;
        public int OperationVersion { get; } = operationVersion;
        public Task HandleAsync(ModuleOperationContext context, JsonElement payload, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
