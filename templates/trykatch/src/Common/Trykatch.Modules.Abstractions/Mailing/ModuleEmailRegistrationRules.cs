using System.Text.RegularExpressions;

namespace Trykatch.Modules;

/// <summary>Validates explicit email metadata without activating adapters or widening business-data grants.</summary>
public static class ModuleEmailRegistrationRules
{
    private static readonly Regex StableKey = new("^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex ModuleId = new("^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static void Validate(IReadOnlyCollection<ModuleDescriptor> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        Dictionary<string, ModuleDescriptor> installed = modules.ToDictionary(module => module.Id, StringComparer.Ordinal);
        HashSet<string> permissions = modules.SelectMany(module => module.Permissions).Select(permission => permission.Key).ToHashSet(StringComparer.Ordinal);
        Dictionary<ModuleEmailTemplateRef, ModuleEmailTemplateDescriptor> templates = [];
        Dictionary<ModuleEmailDisclosurePolicyRef, ModuleEmailDisclosurePolicyDescriptor> policies = [];
        foreach (ModuleDescriptor module in modules)
        {
            Finite(module.EmailTemplates, "templates");
            Finite(module.EmailDisclosurePolicies, "disclosure policies");
            if ((module.EmailTemplates.Count > 0 || module.EmailDisclosurePolicies.Count > 0)
                && !module.Capabilities.HasFlag(ModuleCapabilities.BackgroundWork))
                throw Invalid("Email declarations require the BackgroundWork capability.");

            foreach (ModuleEmailTemplateDescriptor template in module.EmailTemplates)
            {
                Template(new(template.OwnerModuleId, template.TemplateKey, template.TemplateVersion));
                if (template.OwnerModuleId != module.Id || template.MaximumTemplateDataBytes is < 1 or > 65_536)
                    throw Invalid("Email template ownership or data bound is invalid.");
                if (!templates.TryAdd(new(template.OwnerModuleId, template.TemplateKey, template.TemplateVersion), template))
                    throw Invalid("Duplicate email template version.");
            }
            foreach (ModuleEmailDisclosurePolicyDescriptor policy in module.EmailDisclosurePolicies)
            {
                Policy(new(policy.OwnerModuleId, policy.PolicyKey, policy.PolicyVersion));
                if (policy.OwnerModuleId != module.Id)
                    throw Invalid("Email disclosure policy ownership is invalid.");
                if (!policies.TryAdd(new(policy.OwnerModuleId, policy.PolicyKey, policy.PolicyVersion), policy))
                    throw Invalid("Duplicate email disclosure policy version.");
                Finite(policy.AllowedSourceResourceKeys, "source resources", nonempty: true);
                Finite(policy.RequiredUserPermissionKeys, "user permissions");
                Finite(policy.AllowedTemplates, "allowed templates", nonempty: true);
                Finite(policy.AllowedSystemProducers, "system producer bindings");
                foreach (string resource in policy.AllowedSourceResourceKeys)
                {
                    Key(resource);
                    if (!module.DataResources.Any(candidate => candidate.Name == resource))
                        throw Invalid("Email disclosure policy references a source resource it does not own.");
                }
                foreach (string permission in policy.RequiredUserPermissionKeys)
                {
                    Key(permission);
                    if (!permissions.Contains(permission))
                        throw Invalid("Email disclosure policy requires an unregistered permission.");
                }
                foreach (ModuleEmailTemplateRef reference in policy.AllowedTemplates) Template(reference);
                foreach (ModuleEmailSystemDisclosureBinding binding in policy.AllowedSystemProducers)
                {
                    Producer(binding.Producer);
                    Template(binding.Template);
                    if (!policy.AllowedTemplates.Contains(binding.Template))
                        throw Invalid("Email system binding references a template outside the policy allowlist.");
                }
            }
        }

        foreach (ModuleEmailDisclosurePolicyDescriptor policy in policies.Values)
        {
            // Reciprocal declarations may be dormant when a contributing module is absent.
            // They are versioned references, not reverse dependency edges or authority grants.
            foreach (ModuleEmailTemplateRef template in policy.AllowedTemplates)
                if (installed.ContainsKey(template.OwnerModuleId) && !templates.ContainsKey(template))
                    throw Invalid("Email policy references an unavailable installed template version.");
            foreach (ModuleEmailSystemDisclosureBinding binding in policy.AllowedSystemProducers)
                if (installed.TryGetValue(binding.Producer.ModuleId, out ModuleDescriptor? producer)
                    && !producer.Operations.Any(operation => Matches(operation, binding.Producer)
                        && operation.ExecutionKind is ModuleExecutionKind.CommittedEvent or ModuleExecutionKind.ScheduledSystem))
                    throw Invalid("Email policy references an unavailable installed system operation version.");
        }

        foreach (ModuleDescriptor module in modules)
        foreach (ModuleSystemGrantDescriptor grant in module.SystemGrants)
        {
            Finite(grant.AllowedEmailDisclosurePolicies, "email disclosure grants");
            foreach (ModuleEmailDisclosurePolicyRef reference in grant.AllowedEmailDisclosurePolicies)
            {
                Policy(reference);
                if (!policies.TryGetValue(reference, out ModuleEmailDisclosurePolicyDescriptor? policy))
                    throw Invalid("Email grant references an unavailable disclosure policy version.");
                if (reference.OwnerModuleId != module.Id
                    && !module.Requires.Contains(reference.OwnerModuleId, StringComparer.Ordinal)
                    && !module.OptionalDependencies.Contains(reference.OwnerModuleId, StringComparer.Ordinal))
                    throw Invalid("Email producer must declare a dependency on the source-policy owner.");
                if (!policy.AllowedSystemProducers.Any(binding => binding.Producer.ModuleId == module.Id
                        && module.Operations.Any(operation => Matches(operation, binding.Producer)
                            && operation.SystemGrantKey == grant.GrantKey
                            && grant.AllowedOperationKeys.Contains(operation.OperationKey, StringComparer.Ordinal))))
                    throw Invalid("Email grant has no reciprocal source-policy consent for its operations.");
            }
        }
    }

    private static bool Matches(ModuleOperationDescriptor operation, ModuleEmailProducerRef producer) =>
        operation.ModuleId == producer.ModuleId && operation.OperationKey == producer.OperationKey
        && operation.OperationVersion == producer.OperationVersion;

    private static void Template(ModuleEmailTemplateRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        Owner(reference.OwnerModuleId); Key(reference.TemplateKey); Version(reference.TemplateVersion);
        if (reference.OwnerModuleId.Length + 1 + reference.TemplateKey.Length > 160)
            throw Invalid("Qualified email template identity exceeds its protected storage bound.");
    }

    private static void Policy(ModuleEmailDisclosurePolicyRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        Owner(reference.OwnerModuleId); Key(reference.PolicyKey); Version(reference.PolicyVersion);
    }

    private static void Producer(ModuleEmailProducerRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        Owner(reference.ModuleId); Key(reference.OperationKey); Version(reference.OperationVersion);
    }

    private static void Owner(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 80 || !ModuleId.IsMatch(value))
            throw Invalid("Email declaration requires a stable module identifier.");
    }

    private static void Key(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160 || !StableKey.IsMatch(value))
            throw Invalid("Email declaration requires a bounded stable key.");
    }

    private static void Version(int value)
    {
        if (value < 1) throw Invalid("Email declaration requires a positive version.");
    }

    private static void Finite<T>(IReadOnlyList<T> values, string label, bool nonempty = false)
    {
        if (values is null || values.Count > 100 || nonempty && values.Count == 0
            || values.Distinct().Count() != values.Count || values.Any(value => value is null))
            throw Invalid($"Email {label} must be a finite unique set.");
    }

    private static InvalidOperationException Invalid(string message) => new(message);
}
