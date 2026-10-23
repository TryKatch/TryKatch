using System.Text;
using System.Text.Json;
using Trykatch.Application.Outbox;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Eventing;

internal sealed record ModuleEventConsumerRegistration(
    ModuleEventConsumerDescriptor Descriptor,
    ModuleOperationDescriptor Operation,
    ModuleSystemGrantDescriptor Grant,
    IModuleEventConsumer Consumer);

/// <summary>Resolves installed event schemas and consumer implementations through explicit bindings.</summary>
internal sealed class ModuleEventRegistry
{
    public const string InvalidSourceCode = "source_event_invalid";

    private readonly Dictionary<string, ModuleEventConsumerRegistration> _consumers;
    private readonly Dictionary<(string EventKey, int SchemaVersion), IntegrationEventDescriptor> _events;
    private readonly Dictionary<(string OperationKey, int Version), ModuleOperationDescriptor> _operations;
    private readonly Dictionary<(string ModuleId, string GrantKey), ModuleSystemGrantDescriptor> _grants;

    public ModuleEventRegistry(ModuleCatalog catalog, IEnumerable<ModuleEventConsumerBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(bindings);

        _events = catalog.Descriptors
            .SelectMany(module => module.IntegrationEvents)
            .ToDictionary(item => (item.EventKey, item.SchemaVersion));
        _operations = catalog.Descriptors
            .SelectMany(module => module.Operations)
            .ToDictionary(item => (item.OperationKey, item.OperationVersion));
        _grants = catalog.Descriptors
            .SelectMany(module => module.SystemGrants)
            .ToDictionary(item => (item.OwnerModuleId, item.GrantKey));

        Dictionary<(string ModuleId, string ConsumerKey), ModuleEventConsumerDescriptor> declared = catalog.Descriptors
            .SelectMany(module => module.EventConsumers)
            .ToDictionary(item => (item.OwnerModuleId, item.ConsumerKey));
        Dictionary<(string ModuleId, string ConsumerKey), IModuleEventConsumer> supplied = [];
        foreach (ModuleEventConsumerBinding binding in bindings)
        {
            ArgumentNullException.ThrowIfNull(binding);
            ArgumentNullException.ThrowIfNull(binding.Consumer);
            var key = (binding.ModuleId, binding.ConsumerKey);
            if (!declared.ContainsKey(key))
                throw new InvalidOperationException($"Event consumer binding '{binding.ModuleId}/{binding.ConsumerKey}' has no matching installed consumer.");
            if (!supplied.TryAdd(key, binding.Consumer))
                throw new InvalidOperationException($"Duplicate event consumer binding '{binding.ModuleId}/{binding.ConsumerKey}'.");
        }

        Dictionary<string, ModuleEventConsumerRegistration> registrations = new(StringComparer.Ordinal);
        foreach (KeyValuePair<(string ModuleId, string ConsumerKey), ModuleEventConsumerDescriptor> item in declared)
        {
            (string moduleId, string consumerKey) = item.Key;
            ModuleEventConsumerDescriptor descriptor = item.Value;
            var key = (moduleId, consumerKey);
            if (!supplied.TryGetValue(key, out IModuleEventConsumer? consumer))
                throw new InvalidOperationException($"Missing event consumer binding '{moduleId}/{consumerKey}'.");
            if (!_operations.TryGetValue((descriptor.OperationKey, descriptor.OperationVersion), out ModuleOperationDescriptor? operation)
                || operation is null
                || operation.ModuleId != moduleId
                || operation.ExecutionKind != ModuleExecutionKind.CommittedEvent
                || operation.SystemGrantKey != descriptor.SystemGrantKey
                || !_grants.TryGetValue((moduleId, descriptor.SystemGrantKey), out ModuleSystemGrantDescriptor? grant)
                || grant is null
                || grant.OwnerModuleId != moduleId
                || !grant.AllowedOperationKeys.Contains(operation.OperationKey, StringComparer.Ordinal)
                || !grant.AllowedResourceKeys.Contains(operation.ResourcePolicyKey, StringComparer.Ordinal))
                throw new InvalidOperationException($"Event consumer '{moduleId}/{consumerKey}' has no compatible committed-event operation and finite grant.");

            registrations.Add(consumerKey, new(descriptor, operation, grant, consumer));
        }
        _consumers = registrations;
    }

    public IReadOnlyList<ModuleEventConsumerRegistration> ConsumersFor(string eventKey, int schemaVersion) =>
        _consumers.Values
            .Where(registration => registration.Descriptor.AcceptedEvents.Any(accepted =>
                accepted.EventKey == eventKey && accepted.SchemaVersion == schemaVersion))
            .OrderBy(registration => registration.Descriptor.ConsumerKey, StringComparer.Ordinal)
            .ToArray();

    public bool TryGet(string consumerKey, out ModuleEventConsumerRegistration? registration) =>
        _consumers.TryGetValue(consumerKey, out registration);

    public bool TryGetEvent(string eventKey, int schemaVersion, out IntegrationEventDescriptor? descriptor) =>
        _events.TryGetValue((eventKey, schemaVersion), out descriptor);

    public bool TryGetSourceOperation(string operationKey, int operationVersion, out ModuleOperationDescriptor? descriptor)
    {
        if (_operations.TryGetValue((operationKey, operationVersion), out descriptor)) return true;
        descriptor = null;
        return false;
    }

    public bool TryValidateSource(
        OutboxContext context,
        string payload,
        out IntegrationEventDescriptor? descriptor,
        out string? failureCode)
    {
        descriptor = null;
        failureCode = InvalidSourceCode;
        if (context is null || string.IsNullOrWhiteSpace(payload)
            || !_events.TryGetValue((context.EventKey, context.SchemaVersion), out IntegrationEventDescriptor? eventDescriptor)
            || !_operations.TryGetValue((context.OperationKey, context.OperationVersion), out ModuleOperationDescriptor? operation)
            || operation.ModuleId != eventDescriptor.OwnerModuleId
            || !TryValidateSourceAuthority(context, operation))
            return false;

        try
        {
            if (Encoding.UTF8.GetByteCount(payload) > eventDescriptor.MaximumPayloadBytes) return false;
            using JsonDocument document = JsonDocument.Parse(payload);
            object? parsed = eventDescriptor.ReadPayload(document.RootElement);
            if (parsed is null || parsed.GetType() != eventDescriptor.PayloadType) return false;
        }
        catch (Exception)
        {
            return false;
        }

        descriptor = eventDescriptor;
        failureCode = null;
        return true;
    }

    private bool TryValidateSourceAuthority(OutboxContext context, ModuleOperationDescriptor operation)
    {
        if (context.OrganizationId == Guid.Empty || context.EffectiveActorId == Guid.Empty
            || context.OriginActorId == Guid.Empty || context.OperationVersion < 1
            || operation.OperationKey != context.OperationKey || operation.OperationVersion != context.OperationVersion)
            return false;

        if (context.ExecutionKind == "UserCommand")
            return operation.ExecutionKind == ModuleExecutionKind.UserCommand
                && operation.SystemGrantKey is null
                && context.OriginActorId is { } originActorId
                && originActorId == context.EffectiveActorId
                && context.MembershipId is { } membershipId
                && membershipId != Guid.Empty;

        if (context.ExecutionKind is not ("CommittedEvent" or "ScheduledSystem")
            || context.MembershipId is not null || operation.SystemGrantKey is null
            || !_grants.TryGetValue((operation.ModuleId, operation.SystemGrantKey), out ModuleSystemGrantDescriptor? grant)
            || grant is null)
            return false;

        ModuleExecutionKind expectedKind = context.ExecutionKind == "CommittedEvent"
            ? ModuleExecutionKind.CommittedEvent
            : ModuleExecutionKind.ScheduledSystem;
        return operation.ExecutionKind == expectedKind
            && grant.OwnerModuleId == operation.ModuleId
            && grant.AuditActorId == context.EffectiveActorId
            && grant.AllowedOperationKeys.Contains(operation.OperationKey, StringComparer.Ordinal)
            && grant.AllowedResourceKeys.Contains(operation.ResourcePolicyKey, StringComparer.Ordinal);
    }

}
