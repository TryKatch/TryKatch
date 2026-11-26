using System.Text.RegularExpressions;

namespace Trykatch.ModuleTool;

/// <summary>Validates declarative realtime policy metadata without loading module assemblies.</summary>
internal static class ModuleRealtimeManifestRules
{
    private const int MaximumCollectionSize = 100;
    private const int MaximumPolicyIdentityLength = 160;
    private static readonly Regex StableKey = new(
        "^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex StableModuleId = new(
        "^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly HashSet<string> AudienceKinds = new(StringComparer.Ordinal)
    {
        "organization", "user", "resource"
    };

    internal static void Validate(IReadOnlyCollection<ModuleWorkspace.LoadedModule> modules, List<string> errors)
    {
        ModuleWorkspace.LoadedModule[] enabled = modules.Where(module => module.Registration.Enabled).ToArray();
        Dictionary<string, ModuleWorkspace.LoadedModule> installed = new(StringComparer.Ordinal);
        Dictionary<EventIdentity, string> events = [];
        foreach (ModuleWorkspace.LoadedModule module in enabled)
        {
            installed.TryAdd(module.Manifest.Id, module);
            foreach (ModuleIntegrationEventManifest integrationEvent in module.Manifest.Execution.IntegrationEvents)
            {
                if (integrationEvent is null) continue;
                events.TryAdd(new(integrationEvent.EventKey, integrationEvent.SchemaVersion), module.Manifest.Id);
            }
        }

        Dictionary<PolicyIdentity, (ModuleWorkspace.LoadedModule Owner, ModuleRealtimeAudiencePolicyManifest Value)> policies = [];
        foreach (ModuleWorkspace.LoadedModule module in enabled)
        {
            string moduleId = module.Manifest.Id;
            ValidateFinite(module.Manifest.Execution.RealtimeAudiencePolicies, moduleId, "audience policies", errors,
                keySelector: value => PolicyKey(moduleId, value));

            foreach (ModuleRealtimeAudiencePolicyManifest? policy in module.Manifest.Execution.RealtimeAudiencePolicies ?? [])
            {
                if (policy is null) continue;
                Key(moduleId, policy.PolicyKey, "audience policy", errors);
                Version(moduleId, policy.PolicyVersion, "audience policy", errors);
                if ((long)moduleId.Length + 1 + policy.PolicyKey.Length > MaximumPolicyIdentityLength)
                    Error(moduleId, "realtime audience policy identity exceeds its bounded key length");

                PolicyIdentity identity = new(moduleId, policy.PolicyKey, policy.PolicyVersion);
                if (!policies.TryAdd(identity, (module, policy)))
                    Error(moduleId, $"duplicate realtime audience policy '{policy.PolicyKey}' version {policy.PolicyVersion}");

                ValidateFinite(policy.AllowedAudiences, moduleId, "allowed realtime audiences", errors,
                    nonempty: true, keySelector: value => value ?? "<null>");
                HashSet<string> audiences = new(StringComparer.Ordinal);
                foreach (string? audience in policy.AllowedAudiences ?? [])
                {
                    if (audience is null) continue;
                    audiences.Add(audience);
                    if (!AudienceKinds.Contains(audience))
                        Error(moduleId, $"realtime audience policy '{policy.PolicyKey}' declares an unknown audience kind");
                }

                ValidateFinite(policy.AllowedResourceKeys, moduleId, "realtime resource keys", errors,
                    keySelector: value => value ?? "<null>");
                foreach (string? resourceKey in policy.AllowedResourceKeys ?? [])
                {
                    if (resourceKey is null) continue;
                    Key(moduleId, resourceKey, "realtime resource", errors);
                    if (module.Manifest.DataOwnership?.Resources.All(resource => resource.Name != resourceKey) != false)
                        Error(moduleId, $"realtime audience policy '{policy.PolicyKey}' references a resource its owner does not declare");
                }
                if (audiences.Contains("resource") && (policy.AllowedResourceKeys?.Count ?? 0) == 0)
                    Error(moduleId, $"resource realtime audiences in policy '{policy.PolicyKey}' require at least one declared resource key");

                ValidateFinite(policy.RequiredUserPermissionKeys, moduleId, "realtime recipient permissions", errors,
                    keySelector: value => value ?? "<null>");
                foreach (string? permissionKey in policy.RequiredUserPermissionKeys ?? [])
                {
                    if (permissionKey is null) continue;
                    Key(moduleId, permissionKey, "realtime recipient permission", errors);
                    if (!module.Manifest.Contributions.Permissions.Contains(permissionKey, StringComparer.Ordinal))
                        Error(moduleId, $"realtime audience policy '{policy.PolicyKey}' references a permission its owner does not declare");
                }
                if (audiences.Contains("organization") && (policy.RequiredUserPermissionKeys?.Count ?? 0) == 0)
                    Error(moduleId, $"organization realtime audiences in policy '{policy.PolicyKey}' require a recipient permission");

                ValidateFinite(policy.AllowedEvents, moduleId, "realtime allowed event versions", errors,
                    nonempty: true, keySelector: EventKey);
                foreach (ModuleRealtimeEventVersionManifest? allowedEvent in policy.AllowedEvents ?? [])
                {
                    if (allowedEvent is null) continue;
                    Key(moduleId, allowedEvent.EventKey, "realtime event", errors);
                    Version(moduleId, allowedEvent.SchemaVersion, "realtime event", errors);
                    if (!allowedEvent.EventKey.StartsWith(moduleId + ".", StringComparison.Ordinal))
                        Error(moduleId, $"realtime audience policy '{policy.PolicyKey}' contains an event outside its owner namespace");
                    if (!events.TryGetValue(new(allowedEvent.EventKey, allowedEvent.SchemaVersion), out string? eventOwner)
                        || eventOwner != moduleId)
                        Error(moduleId, $"realtime audience policy '{policy.PolicyKey}' references an unavailable owner event version");
                }

                ValidateFinite(policy.AllowedSystemProducers, moduleId, "realtime system producers", errors,
                    keySelector: ProducerKey);
                foreach (ModuleRealtimeProducerManifest? producer in policy.AllowedSystemProducers ?? [])
                {
                    if (producer is null) continue;
                    ModuleId(moduleId, producer.ModuleId, "realtime producer module", errors);
                    Key(moduleId, producer.OperationKey, "realtime producer operation", errors);
                    Version(moduleId, producer.OperationVersion, "realtime producer operation", errors);
                }
            }
        }

        Dictionary<OperationIdentity, (ModuleWorkspace.LoadedModule Owner, ModuleOperationManifest Value)> operations = [];
        Dictionary<GrantIdentity, (ModuleWorkspace.LoadedModule Owner, ModuleSystemGrantManifest Value)> grants = [];
        foreach (ModuleWorkspace.LoadedModule module in enabled)
        {
            foreach (ModuleOperationManifest operation in module.Manifest.Execution.Operations)
            {
                if (operation is null) continue;
                operations.TryAdd(new(module.Manifest.Id, operation.OperationKey, operation.OperationVersion), (module, operation));
            }
            foreach (ModuleSystemGrantManifest grant in module.Manifest.Execution.SystemGrants)
            {
                if (grant is null) continue;
                grants.TryAdd(new(module.Manifest.Id, grant.GrantKey), (module, grant));
            }
        }

        foreach ((PolicyIdentity policyIdentity, (ModuleWorkspace.LoadedModule policyOwner, ModuleRealtimeAudiencePolicyManifest policy)) in policies)
        foreach (ModuleRealtimeProducerManifest? producer in policy.AllowedSystemProducers ?? [])
        {
            if (producer is null) continue;
            if (!installed.TryGetValue(producer.ModuleId, out ModuleWorkspace.LoadedModule? producerModule)
                || !operations.TryGetValue(new(producer.ModuleId, producer.OperationKey, producer.OperationVersion), out var operation)
                || operation.Value.ExecutionKind != "committed-event"
                || operation.Value.SystemGrantKey is null
                || !grants.TryGetValue(new(producer.ModuleId, operation.Value.SystemGrantKey), out var grant)
                || !grant.Value.AllowedOperationKeys.Contains(operation.Value.OperationKey, StringComparer.Ordinal)
                || !ContainsPolicyReference(grant.Value.AllowedRealtimePolicies, policyIdentity)
                || producer.ModuleId != policyOwner.Manifest.Id
                    && !producerModule.Manifest.Requires.Contains(policyOwner.Manifest.Id, StringComparer.Ordinal)
                    && !producerModule.Manifest.OptionalDependencies.Contains(policyOwner.Manifest.Id, StringComparer.Ordinal))
                Error(policyOwner.Manifest.Id, $"realtime producer for policy '{policy.PolicyKey}' lacks an installed committed-event operation, reciprocal grant consent, or owner dependency");
        }

        foreach (ModuleWorkspace.LoadedModule module in enabled)
        foreach (ModuleSystemGrantManifest grant in module.Manifest.Execution.SystemGrants)
        {
            if (grant is null) continue;
            string moduleId = module.Manifest.Id;
            ValidateFinite(grant.AllowedRealtimePolicies, moduleId, "realtime grant policy references", errors,
                keySelector: PolicyRefKey);
            foreach (ModuleRealtimePolicyRefManifest? reference in grant.AllowedRealtimePolicies ?? [])
            {
                if (reference is null) continue;
                ValidatePolicyReference(moduleId, reference, errors);
                PolicyIdentity identity = new(reference.OwnerModuleId, reference.PolicyKey, reference.PolicyVersion);
                if (!policies.TryGetValue(identity, out var policyEntry))
                {
                    Error(moduleId, $"system grant '{grant.GrantKey}' references an unavailable realtime policy version");
                    continue;
                }

                if (reference.OwnerModuleId != moduleId
                    && !module.Manifest.Requires.Contains(reference.OwnerModuleId, StringComparer.Ordinal)
                    && !module.Manifest.OptionalDependencies.Contains(reference.OwnerModuleId, StringComparer.Ordinal))
                    Error(moduleId, $"realtime producer must declare a dependency on policy owner '{reference.OwnerModuleId}'");

                bool reciprocalConsent = (policyEntry.Value.AllowedSystemProducers ?? []).Any(producer =>
                    producer is not null
                    && producer.ModuleId == moduleId
                    && operations.TryGetValue(new(moduleId, producer.OperationKey, producer.OperationVersion), out var operation)
                    && operation.Value.ExecutionKind == "committed-event"
                    && operation.Value.SystemGrantKey == grant.GrantKey
                    && grant.AllowedOperationKeys.Contains(operation.Value.OperationKey, StringComparer.Ordinal));
                if (!reciprocalConsent)
                    Error(moduleId, $"realtime grant '{grant.GrantKey}' has no reciprocal policy consent for its committed-event operation");
            }
        }

        void Error(string id, string message) => errors.Add($"Trykatch module '{id}' {message}.");
    }

    private static void ValidatePolicyReference(string module, ModuleRealtimePolicyRefManifest reference, List<string> errors)
    {
        ModuleId(module, reference.OwnerModuleId, "realtime policy owner", errors);
        Key(module, reference.PolicyKey, "realtime policy reference", errors);
        Version(module, reference.PolicyVersion, "realtime policy reference", errors);
        if ((long)reference.OwnerModuleId.Length + 1 + reference.PolicyKey.Length > MaximumPolicyIdentityLength)
            Error(module, "realtime policy reference exceeds its bounded identity length", errors);
    }

    private static void ValidateFinite<T>(
        IReadOnlyList<T?>? values,
        string module,
        string subject,
        List<string> errors,
        bool nonempty = false,
        Func<T, string>? keySelector = null)
        where T : class
    {
        if (values is null || values.Count > MaximumCollectionSize || nonempty && values.Count == 0)
        {
            Error(module, $"{subject} must be a finite unique realtime set", errors);
            return;
        }

        if (values.Any(value => value is null))
            Error(module, $"{subject} must be a finite unique realtime set", errors);

        IEnumerable<string> keys = keySelector is null
            ? values.Where(value => value is not null).Select(value => value!.ToString() ?? string.Empty)
            : values.Where(value => value is not null).Select(value => keySelector(value!));
        if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Count())
            Error(module, $"{subject} must be a finite unique realtime set", errors);
    }

    private static bool ContainsPolicyReference(
        IReadOnlyList<ModuleRealtimePolicyRefManifest?>? references,
        PolicyIdentity identity) => references?.Any(reference => reference is not null
            && reference.OwnerModuleId == identity.OwnerModuleId
            && reference.PolicyKey == identity.PolicyKey
            && reference.PolicyVersion == identity.PolicyVersion) == true;

    private static string PolicyKey(string owner, ModuleRealtimeAudiencePolicyManifest? value) =>
        value is null ? "<null>" : $"{owner}/{value.PolicyKey}/{value.PolicyVersion}";

    private static string PolicyRefKey(ModuleRealtimePolicyRefManifest? value) =>
        value is null ? "<null>" : $"{value.OwnerModuleId}/{value.PolicyKey}/{value.PolicyVersion}";

    private static string EventKey(ModuleRealtimeEventVersionManifest? value) =>
        value is null ? "<null>" : $"{value.EventKey}/{value.SchemaVersion}";

    private static string ProducerKey(ModuleRealtimeProducerManifest? value) =>
        value is null ? "<null>" : $"{value.ModuleId}/{value.OperationKey}/{value.OperationVersion}";

    private static void ModuleId(string module, string? value, string subject, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 80 || !StableModuleId.IsMatch(value))
            Error(module, $"{subject} requires a stable module identifier", errors);
    }

    private static void Key(string module, string? value, string subject, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160 || !StableKey.IsMatch(value))
            Error(module, $"{subject} requires a bounded stable lower-case key", errors);
    }

    private static void Version(string module, int value, string subject, List<string> errors)
    {
        if (value < 1) Error(module, $"{subject} requires a positive version", errors);
    }

    private static void Error(string module, string message, List<string> errors) =>
        errors.Add($"Trykatch module '{module}' {message}.");

    private readonly record struct EventIdentity(string EventKey, int SchemaVersion);
    private readonly record struct PolicyIdentity(string OwnerModuleId, string PolicyKey, int PolicyVersion);
    private readonly record struct OperationIdentity(string ModuleId, string OperationKey, int OperationVersion);
    private readonly record struct GrantIdentity(string ModuleId, string GrantKey);
}
