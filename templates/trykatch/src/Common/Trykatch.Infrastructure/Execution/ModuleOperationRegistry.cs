using Trykatch.Modules;

namespace Trykatch.Infrastructure.Execution;

/// <summary>The validated adapter set for one installed module operation.</summary>
internal sealed record ModuleOperationRegistration(
    ModuleOperationDescriptor Descriptor,
    IModuleOperationHandler Handler,
    IModuleOperationResourcePolicy ResourcePolicy,
    ModuleSystemGrantDescriptor? SystemGrant);

/// <summary>Resolves installed operations only through explicit module-owned adapters.</summary>
internal sealed class ModuleOperationRegistry
{
    public const int CatalogVersion = 1;

    private readonly ModuleCatalog _catalog;
    private readonly Dictionary<(string ModuleId, string OperationKey, int Version), ModuleOperationRegistration> _registrations;

    public ModuleOperationRegistry(
        ModuleCatalog catalog,
        IEnumerable<IModuleOperationHandler> handlers,
        IEnumerable<IModuleOperationResourcePolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(policies);
        _catalog = catalog;

        ModuleOperationDescriptor[] operations = catalog.Descriptors
            .SelectMany(module => module.Operations)
            .ToArray();
        Dictionary<(string ModuleId, string OperationKey, int Version), IModuleOperationHandler> handlerMap =
            BuildHandlerMap(operations, handlers);

        Dictionary<(string ModuleId, string PolicyKey), IModuleOperationResourcePolicy> policyMap =
            BuildPolicyMap(operations, policies);
        Dictionary<string, ModuleSystemGrantDescriptor> grants = catalog.Descriptors
            .SelectMany(module => module.SystemGrants)
            .ToDictionary(grant => $"{grant.OwnerModuleId}\0{grant.GrantKey}", StringComparer.Ordinal);

        Dictionary<(string ModuleId, string OperationKey, int Version), ModuleOperationRegistration> registrations = [];
        foreach (ModuleOperationDescriptor operation in operations)
        {
            ModuleSystemGrantDescriptor? grant = null;
            if (operation.SystemGrantKey is not null
                && !grants.TryGetValue($"{operation.ModuleId}\0{operation.SystemGrantKey}", out grant))
                throw new InvalidOperationException($"Operation '{operation.ModuleId}/{operation.OperationKey}' references an unavailable system grant.");

            var key = (operation.ModuleId, operation.OperationKey, operation.OperationVersion);
            registrations.Add(key, new ModuleOperationRegistration(
                operation,
                handlerMap[key],
                policyMap[(operation.ModuleId, operation.ResourcePolicyKey)],
                grant));
        }
        _registrations = registrations;
    }

    public bool ContainsModule(string moduleId) => _catalog.Contains(moduleId);

    public bool TryGet(
        string moduleId,
        string operationKey,
        int operationVersion,
        out ModuleOperationRegistration? registration) =>
        _registrations.TryGetValue((moduleId, operationKey, operationVersion), out registration);

    public bool AcceptsSourceEvent(ModuleOperationRegistration registration, string eventKey, int schemaVersion)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (!_registrations.TryGetValue(
                (registration.Descriptor.ModuleId, registration.Descriptor.OperationKey, registration.Descriptor.OperationVersion),
                out ModuleOperationRegistration? installed)
            || !ReferenceEquals(installed, registration)
            || registration.Descriptor.ExecutionKind != ModuleExecutionKind.CommittedEvent
            || registration.SystemGrant is null)
            return false;

        return _catalog.Descriptors
            .Where(module => module.Id == registration.Descriptor.ModuleId)
            .SelectMany(module => module.EventConsumers)
            .Any(consumer =>
                consumer.OperationKey == registration.Descriptor.OperationKey
                && consumer.OperationVersion == registration.Descriptor.OperationVersion
                && consumer.SystemGrantKey == registration.SystemGrant.GrantKey
                && registration.SystemGrant.AllowedOperationKeys.Contains(registration.Descriptor.OperationKey, StringComparer.Ordinal)
                && consumer.AcceptedEvents.Any(accepted => accepted.EventKey == eventKey && accepted.SchemaVersion == schemaVersion));
    }

    private static Dictionary<(string ModuleId, string OperationKey, int Version), IModuleOperationHandler> BuildHandlerMap(
        IReadOnlyCollection<ModuleOperationDescriptor> operations,
        IEnumerable<IModuleOperationHandler> handlers)
    {
        HashSet<(string ModuleId, string OperationKey, int Version)> declared = operations
            .Select(operation => (operation.ModuleId, operation.OperationKey, operation.OperationVersion))
            .ToHashSet();
        Dictionary<(string ModuleId, string OperationKey, int Version), IModuleOperationHandler> result = [];
        foreach (IModuleOperationHandler handler in handlers)
        {
            ArgumentNullException.ThrowIfNull(handler);
            var key = (handler.ModuleId, handler.OperationKey, handler.OperationVersion);
            if (!declared.Contains(key))
                throw new InvalidOperationException($"Handler '{handler.ModuleId}/{handler.OperationKey}' has no matching installed operation version.");
            if (!result.TryAdd(key, handler))
                throw new InvalidOperationException($"Duplicate handler for installed operation '{handler.ModuleId}/{handler.OperationKey}' version {handler.OperationVersion}.");
        }

        foreach (var key in declared)
        {
            if (!result.ContainsKey(key))
                throw new InvalidOperationException($"Missing handler for installed operation '{key.ModuleId}/{key.OperationKey}' version {key.Version}.");
        }
        return result;
    }

    private static Dictionary<(string ModuleId, string PolicyKey), IModuleOperationResourcePolicy> BuildPolicyMap(
        IReadOnlyCollection<ModuleOperationDescriptor> operations,
        IEnumerable<IModuleOperationResourcePolicy> policies)
    {
        HashSet<(string ModuleId, string PolicyKey)> declared = operations
            .Select(operation => (operation.ModuleId, operation.ResourcePolicyKey))
            .ToHashSet();
        Dictionary<(string ModuleId, string PolicyKey), IModuleOperationResourcePolicy> result = [];
        foreach (IModuleOperationResourcePolicy policy in policies)
        {
            ArgumentNullException.ThrowIfNull(policy);
            var key = (policy.ModuleId, policy.PolicyKey);
            if (!declared.Contains(key))
                throw new InvalidOperationException($"Resource policy '{policy.ModuleId}/{policy.PolicyKey}' has no matching installed operation.");
            if (!result.TryAdd(key, policy))
                throw new InvalidOperationException($"Duplicate resource policy for '{policy.ModuleId}/{policy.PolicyKey}'.");
        }

        foreach (var key in declared)
        {
            if (!result.ContainsKey(key))
                throw new InvalidOperationException($"Missing resource policy for '{key.ModuleId}/{key.PolicyKey}'.");
        }
        return result;
    }
}
