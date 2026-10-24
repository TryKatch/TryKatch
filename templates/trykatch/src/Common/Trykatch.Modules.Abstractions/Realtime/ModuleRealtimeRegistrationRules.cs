using System.Text.RegularExpressions;

namespace Trykatch.Modules;

/// <summary>Validates finite realtime declarations without activating adapters or infrastructure.</summary>
public static class ModuleRealtimeRegistrationRules
{
    private const int MaximumCollectionSize = 100;
    private static readonly Regex StableKey = new(
        "^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex StableModuleId = new(
        "^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static void Validate(IReadOnlyCollection<ModuleDescriptor> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        Dictionary<string, ModuleDescriptor> installed = [];
        Dictionary<ModuleRealtimePolicyRef, ModuleRealtimeAudiencePolicyDescriptor> policies = [];
        Dictionary<(string EventKey, int Version), string> events = [];

        foreach (ModuleDescriptor module in modules)
        {
            if (module is null)
                throw Invalid("Realtime registration contains a null module descriptor.");
            ModuleId(module.Id);
            if (!installed.TryAdd(module.Id, module))
                throw Invalid("Realtime registration contains a duplicate installed module identifier.");
            Finite(module.Permissions, "module permissions", maximumCount: null);
            foreach (PermissionDescriptor permission in module.Permissions)
            {
                if (permission is null)
                    throw Invalid("Realtime registration contains a null permission.");
                Key(permission.Key);
            }

            Finite(module.DataResources, "module data resources", maximumCount: null);
            Finite(module.IntegrationEvents, "integration events", maximumCount: null);
            foreach (IntegrationEventDescriptor integrationEvent in module.IntegrationEvents)
            {
                if (integrationEvent is null || integrationEvent.OwnerModuleId != module.Id)
                    throw Invalid("Realtime registration contains an event with invalid ownership.");
                Key(integrationEvent.EventKey);
                Version(integrationEvent.SchemaVersion);
                if (!events.TryAdd((integrationEvent.EventKey, integrationEvent.SchemaVersion), module.Id))
                    throw Invalid("Realtime registration contains a duplicate installed event version.");
            }
        }

        foreach (ModuleDescriptor module in modules)
        {
            Finite(module.RealtimeAudiencePolicies, "audience policies");
            foreach (ModuleRealtimeAudiencePolicyDescriptor policy in module.RealtimeAudiencePolicies)
            {
                if (policy is null || policy.OwnerModuleId != module.Id)
                    throw Invalid("Realtime audience policy ownership is invalid.");
                ModuleRealtimePolicyRef reference = new(policy.OwnerModuleId, policy.PolicyKey, policy.PolicyVersion);
                Policy(reference);
                if (!policies.TryAdd(reference, policy))
                    throw Invalid("Realtime audience policy version is duplicated.");

                Finite(policy.AllowedAudiences, "allowed audience kinds", nonempty: true);
                foreach (ModuleRealtimeAudienceKind kind in policy.AllowedAudiences)
                    if (!Enum.IsDefined(kind))
                        throw Invalid("Realtime audience policy contains an unknown audience kind.");

                Finite(policy.AllowedResourceKeys, "allowed resource keys");
                foreach (string resourceKey in policy.AllowedResourceKeys)
                {
                    Key(resourceKey);
                    if (!module.DataResources.Any(resource => resource.Name == resourceKey))
                        throw Invalid("Realtime audience policy references a resource the owner does not declare.");
                }
                if (policy.AllowedAudiences.Contains(ModuleRealtimeAudienceKind.Resource)
                    && policy.AllowedResourceKeys.Count == 0)
                    throw Invalid("Resource realtime audiences require at least one declared resource key.");

                Finite(policy.RequiredUserPermissionKeys, "required recipient permissions");
                if (policy.AllowedAudiences.Contains(ModuleRealtimeAudienceKind.Organization)
                    && policy.RequiredUserPermissionKeys.Count == 0)
                    throw Invalid("Organization realtime audiences require a recipient permission.");
                foreach (string permission in policy.RequiredUserPermissionKeys)
                {
                    Key(permission);
                    if (!module.Permissions.Any(candidate => candidate.Key == permission))
                        throw Invalid("Realtime audience policy references a permission its owner does not declare.");
                }

                Finite(policy.AllowedEvents, "allowed event versions", nonempty: true);
                foreach (ModuleEventVersion allowedEvent in policy.AllowedEvents)
                {
                    if (allowedEvent is null)
                        throw Invalid("Realtime audience policy contains a null event version.");
                    EventKey(allowedEvent.EventKey, policy.OwnerModuleId);
                    Version(allowedEvent.SchemaVersion);
                    if (!events.TryGetValue((allowedEvent.EventKey, allowedEvent.SchemaVersion), out string? owner)
                        || owner != policy.OwnerModuleId)
                        throw Invalid("Realtime audience policy references an unavailable owner event version.");
                }

                Finite(policy.AllowedSystemProducers, "allowed committed-event producers");
                foreach (ModuleRealtimeProducerRef producer in policy.AllowedSystemProducers)
                    Producer(producer);
            }
        }

        Dictionary<(string ModuleId, string OperationKey, int OperationVersion), ModuleOperationDescriptor> operations = [];
        Dictionary<(string ModuleId, string GrantKey), ModuleSystemGrantDescriptor> grants = [];
        foreach (ModuleDescriptor module in modules)
        {
            Finite(module.Operations, "module operations", maximumCount: null);
            foreach (ModuleOperationDescriptor operation in module.Operations)
            {
                if (operation is null || operation.ModuleId != module.Id)
                    throw Invalid("Realtime registration contains an operation with invalid ownership.");
                Key(operation.OperationKey);
                Version(operation.OperationVersion);
                if (!operations.TryAdd((module.Id, operation.OperationKey, operation.OperationVersion), operation))
                    throw Invalid("Realtime registration contains a duplicate operation version.");
            }

            Finite(module.SystemGrants, "module system grants", maximumCount: null);
            foreach (ModuleSystemGrantDescriptor grant in module.SystemGrants)
            {
                if (grant is null || grant.OwnerModuleId != module.Id)
                    throw Invalid("Realtime registration contains a grant with invalid ownership.");
                Key(grant.GrantKey);
                if (!grants.TryAdd((module.Id, grant.GrantKey), grant))
                    throw Invalid("Realtime registration contains a duplicate system grant.");
            }
        }

        foreach ((ModuleRealtimePolicyRef policyReference, ModuleRealtimeAudiencePolicyDescriptor policy) in policies)
        foreach (ModuleRealtimeProducerRef producer in policy.AllowedSystemProducers)
        {
            if (!installed.TryGetValue(producer.ModuleId, out ModuleDescriptor? producerModule)
                || !operations.TryGetValue((producer.ModuleId, producer.OperationKey, producer.OperationVersion), out ModuleOperationDescriptor? operation)
                || operation.ExecutionKind != ModuleExecutionKind.CommittedEvent
                || operation.SystemGrantKey is null
                || !grants.TryGetValue((producer.ModuleId, operation.SystemGrantKey), out ModuleSystemGrantDescriptor? grant)
                || !grant.AllowedOperationKeys.Contains(operation.OperationKey, StringComparer.Ordinal)
                || !grant.AllowedRealtimePolicies.Contains(policyReference)
                || producer.ModuleId != policy.OwnerModuleId
                    && !producerModule.Requires.Contains(policy.OwnerModuleId, StringComparer.Ordinal)
                    && !producerModule.OptionalDependencies.Contains(policy.OwnerModuleId, StringComparer.Ordinal))
                throw Invalid("Realtime producer is not an installed committed-event operation with reciprocal grant and dependency authority.");
        }

        foreach (ModuleDescriptor module in modules)
        foreach (ModuleSystemGrantDescriptor grant in module.SystemGrants)
        {
            Finite(grant.AllowedOperationKeys, "grant operation keys", maximumCount: null);
            foreach (string operationKey in grant.AllowedOperationKeys)
                Key(operationKey);
            Finite(grant.AllowedRealtimePolicies, "grant realtime policy references");
            foreach (ModuleRealtimePolicyRef policyReference in grant.AllowedRealtimePolicies)
            {
                Policy(policyReference);
                if (!policies.TryGetValue(policyReference, out ModuleRealtimeAudiencePolicyDescriptor? policy)
                    || !policy.AllowedSystemProducers.Any(producer => producer.ModuleId == module.Id
                        && module.Operations.Any(operation => operation.OperationKey == producer.OperationKey
                            && operation.OperationVersion == producer.OperationVersion
                            && operation.ExecutionKind == ModuleExecutionKind.CommittedEvent
                            && operation.SystemGrantKey == grant.GrantKey
                            && grant.AllowedOperationKeys.Contains(operation.OperationKey, StringComparer.Ordinal)))
                    || policyReference.OwnerModuleId != module.Id
                        && !module.Requires.Contains(policyReference.OwnerModuleId, StringComparer.Ordinal)
                        && !module.OptionalDependencies.Contains(policyReference.OwnerModuleId, StringComparer.Ordinal))
                    throw Invalid("Realtime grant has no matching reciprocal source-policy consent and committed-event operation.");
            }
        }
    }

    public static void ValidateAudience(ModuleRealtimeAudience audience)
    {
        if (audience is null)
            throw Invalid("Realtime audience is required.");
        Policy(audience.Policy);
        if (!Enum.IsDefined(audience.Kind))
            throw Invalid("Realtime audience kind is unknown.");
        if (audience.Resource is not null)
        {
            Key(audience.Resource.ResourceKey);
            if (audience.Resource.ResourceId == Guid.Empty)
                throw Invalid("Realtime resource audience requires a nonempty resource identifier.");
        }

        switch (audience.Kind)
        {
            case ModuleRealtimeAudienceKind.Organization when audience.UserId is not null:
                throw Invalid("Organization realtime audiences cannot select a user.");
            case ModuleRealtimeAudienceKind.User when audience.UserId is null || audience.UserId == Guid.Empty:
                throw Invalid("User realtime audiences require a nonempty user identifier.");
            case ModuleRealtimeAudienceKind.Resource when audience.UserId is not null || audience.Resource is null:
                throw Invalid("Resource realtime audiences require exactly one resource selector.");
        }
    }

    public static void ValidateNotification(ModuleRealtimeNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ModuleId(notification.ModuleId);
        EventKey(notification.EventKey, notification.ModuleId);
        Version(notification.SchemaVersion);
        if (notification.EventId == Guid.Empty)
            throw Invalid("Realtime invalidation requires a nonempty event identifier.");
        ValidateAudience(notification.Audience);
    }

    public static void ValidateBindings(
        IReadOnlyCollection<ModuleDescriptor> modules,
        IReadOnlyCollection<ModuleRealtimeAudiencePolicyBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(bindings);
        Validate(modules);
        Finite(bindings, "audience policy bindings", maximumCount: null);

        HashSet<ModuleRealtimePolicyRef> installed = modules
            .SelectMany(module => module.RealtimeAudiencePolicies)
            .Select(policy => new ModuleRealtimePolicyRef(policy.OwnerModuleId, policy.PolicyKey, policy.PolicyVersion))
            .ToHashSet();
        HashSet<ModuleRealtimePolicyRef> bound = [];
        foreach (ModuleRealtimeAudiencePolicyBinding binding in bindings)
        {
            if (binding is null || binding.Adapter is null)
                throw Invalid("Realtime audience policy binding requires an explicit adapter.");
            Policy(binding.Policy);
            if (!installed.Contains(binding.Policy) || !bound.Add(binding.Policy))
                throw Invalid("Realtime audience policy binding is unknown or duplicated.");
        }

        if (!installed.SetEquals(bound))
            throw Invalid("Every installed realtime audience policy requires exactly one adapter binding.");
    }

    private static void Policy(ModuleRealtimePolicyRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ModuleId(reference.OwnerModuleId);
        Key(reference.PolicyKey);
        Version(reference.PolicyVersion);
        if (reference.OwnerModuleId.Length + 1 + reference.PolicyKey.Length > 160)
            throw Invalid("Qualified realtime policy identity exceeds its storage bound.");
    }

    private static void Producer(ModuleRealtimeProducerRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ModuleId(reference.ModuleId);
        Key(reference.OperationKey);
        Version(reference.OperationVersion);
    }

    private static void EventKey(string value, string ownerModuleId)
    {
        Key(value);
        if (!value.StartsWith(ownerModuleId + ".", StringComparison.Ordinal))
            throw Invalid("Realtime event key must belong to the policy owner module.");
    }

    private static void ModuleId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 80 || !StableModuleId.IsMatch(value))
            throw Invalid("Realtime declaration requires a stable module identifier.");
    }

    private static void Key(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160 || !StableKey.IsMatch(value))
            throw Invalid("Realtime declaration requires a bounded stable key.");
    }

    private static void Version(int value)
    {
        if (value < 1)
            throw Invalid("Realtime declaration requires a positive version.");
    }

    private static void Finite<T>(
        IReadOnlyCollection<T> values,
        string label,
        bool nonempty = false,
        int? maximumCount = MaximumCollectionSize)
    {
        if (values is null || maximumCount is int maximum && values.Count > maximum || nonempty && values.Count == 0
            || values.Distinct().Count() != values.Count || values.Any(value => value is null))
            throw Invalid($"Realtime {label} must be a finite unique collection.");
    }

    private static InvalidOperationException Invalid(string message) => new(message);
}
