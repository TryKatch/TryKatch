using System.Text.RegularExpressions;

namespace Trykatch.ModuleTool;

/// <summary>Validates declarative email metadata without loading module assemblies.</summary>
internal static class ModuleEmailManifestRules
{
    private const int MaximumSetSize = 100;
    private const int MaximumTemplateDataBytes = 65_536;
    private const int MaximumTemplatePurposeLength = 160;
    private static readonly Regex StableKey = new("^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex ModuleId = new("^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static void Validate(IReadOnlyCollection<ModuleWorkspace.LoadedModule> modules, List<string> errors)
    {
        ModuleWorkspace.LoadedModule[] enabled = modules.Where(module => module.Registration.Enabled).ToArray();
        Dictionary<string, ModuleWorkspace.LoadedModule> installed = new(StringComparer.Ordinal);
        foreach (ModuleWorkspace.LoadedModule module in enabled)
            installed.TryAdd(module.Manifest.Id, module);
        HashSet<string> permissions = enabled.SelectMany(module => module.Manifest.Contributions.Permissions).ToHashSet(StringComparer.Ordinal);
        Dictionary<TemplateIdentity, (string Owner, ModuleEmailTemplateManifest Value)> templates = [];
        Dictionary<PolicyIdentity, (string Owner, ModuleEmailDisclosurePolicyManifest Value)> policies = [];

        foreach (ModuleWorkspace.LoadedModule module in enabled)
        {
            ModuleManifest manifest = module.Manifest;
            ModuleExecutionManifest execution = manifest.Execution;
            string id = manifest.Id;
            bool declaresEmail = execution.EmailTemplates?.Count > 0 || execution.EmailDisclosurePolicies?.Count > 0;
            if (declaresEmail && !manifest.Capabilities.Contains("background-work", StringComparer.Ordinal))
                Error(id, "email declarations require the BackgroundWork capability");

            ValidateFinite(execution.EmailTemplates, id, "email templates", errors);
            foreach (ModuleEmailTemplateManifest template in execution.EmailTemplates ?? [])
            {
                if (template is null) continue; // ValidateFinite reports malformed entries.
                Key(id, template.TemplateKey, "email template", errors);
                Version(id, template.TemplateVersion, "email template", errors);
                if (template.MaximumTemplateDataBytes is < 1 or > MaximumTemplateDataBytes)
                    Error(id, $"email template data limit must be between 1 and {MaximumTemplateDataBytes} bytes");

                var identity = new TemplateIdentity(id, template.TemplateKey, template.TemplateVersion);
                if (PurposeLength(identity.OwnerModuleId, identity.TemplateKey) > MaximumTemplatePurposeLength)
                    Error(id, "email template reference exceeds the bounded purpose key length");
                if (!templates.TryAdd(identity, (id, template)))
                    Error(id, $"duplicate email template '{template.TemplateKey}' version {template.TemplateVersion}");
            }

            ValidateFinite(execution.EmailDisclosurePolicies, id, "email disclosure policies", errors);
            foreach (ModuleEmailDisclosurePolicyManifest policy in execution.EmailDisclosurePolicies ?? [])
            {
                if (policy is null) continue;
                Key(id, policy.PolicyKey, "email disclosure policy", errors);
                Version(id, policy.PolicyVersion, "email disclosure policy", errors);
                var identity = new PolicyIdentity(id, policy.PolicyKey, policy.PolicyVersion);
                if (!policies.TryAdd(identity, (id, policy)))
                    Error(id, $"duplicate email disclosure policy '{policy.PolicyKey}' version {policy.PolicyVersion}");

                ValidateFinite(policy.AllowedSourceResourceKeys, id, "email source resources", errors, nonempty: true);
                foreach (string resource in policy.AllowedSourceResourceKeys ?? [])
                {
                    Key(id, resource, "email source resource", errors);
                    if (manifest.DataOwnership?.Resources.All(item => item.Name != resource) != false)
                        Error(id, $"email disclosure policy '{policy.PolicyKey}' references an unowned source resource '{resource}'");
                }

                ValidateFinite(policy.RequiredUserPermissionKeys, id, "email required permissions", errors);
                foreach (string permission in policy.RequiredUserPermissionKeys ?? [])
                {
                    Key(id, permission, "email required permission", errors);
                    if (!permissions.Contains(permission))
                        Error(id, $"email disclosure policy '{policy.PolicyKey}' requires unregistered permission '{permission}'");
                }

                ValidateFinite(policy.AllowedTemplates, id, "email allowed templates", errors, nonempty: true,
                    value => TemplateKey(value));
                foreach (ModuleEmailTemplateRefManifest reference in policy.AllowedTemplates ?? [])
                    ValidateTemplateReference(id, reference, errors);

                ValidateFinite(policy.AllowedSystemProducers, id, "email system producer bindings", errors,
                    keySelector: value => BindingKey(value));
                foreach (ModuleEmailSystemProducerBindingManifest binding in policy.AllowedSystemProducers ?? [])
                {
                    if (binding?.Producer is null || binding.Template is null)
                    {
                        Error(id, $"email disclosure policy '{policy.PolicyKey}' contains an invalid system producer binding");
                        continue;
                    }

                    ValidateProducer(id, binding.Producer, errors);
                    ValidateTemplateReference(id, binding.Template, errors);
                    if (!(policy.AllowedTemplates ?? []).Any(reference => TemplateKey(reference) == TemplateKey(binding.Template)))
                        Error(id, $"email system binding references a template outside disclosure policy '{policy.PolicyKey}' allowlist");
                }
            }
        }

        foreach ((string owner, ModuleEmailDisclosurePolicyManifest policy) in policies.Values)
        {
            foreach (ModuleEmailTemplateRefManifest reference in policy.AllowedTemplates ?? [])
            {
                if (reference is null) continue;
                if (installed.ContainsKey(reference.OwnerModuleId)
                    && !templates.ContainsKey(new(reference.OwnerModuleId, reference.TemplateKey, reference.TemplateVersion)))
                    Error(owner, $"email policy '{policy.PolicyKey}' references an unavailable installed template version");
            }

            foreach (ModuleEmailSystemProducerBindingManifest binding in policy.AllowedSystemProducers ?? [])
            {
                if (binding?.Producer is not { } producer || !installed.TryGetValue(producer.ModuleId, out ModuleWorkspace.LoadedModule? producerModule))
                    continue;

                bool exactSystemOperation = producerModule.Manifest.Execution.Operations.Any(operation =>
                    operation.OperationKey == producer.OperationKey
                    && operation.OperationVersion == producer.OperationVersion
                    && operation.ExecutionKind is "committed-event" or "scheduled-system");
                if (!exactSystemOperation)
                    Error(owner, $"email policy '{policy.PolicyKey}' references an unavailable installed system operation version");
            }
        }

        foreach (ModuleWorkspace.LoadedModule module in enabled)
        foreach (ModuleSystemGrantManifest grant in module.Manifest.Execution.SystemGrants)
        {
            string id = module.Manifest.Id;
            ValidateFinite(grant.AllowedEmailDisclosurePolicies, id, "email disclosure grants", errors,
                keySelector: value => PolicyKey(value));
            foreach (ModuleEmailDisclosurePolicyRefManifest reference in grant.AllowedEmailDisclosurePolicies ?? [])
            {
                if (reference is null) continue;
                ValidatePolicyReference(id, reference, errors);
                var identity = new PolicyIdentity(reference.OwnerModuleId, reference.PolicyKey, reference.PolicyVersion);
                if (!policies.TryGetValue(identity, out var policyEntry))
                {
                    Error(id, $"system grant '{grant.GrantKey}' references an unavailable email disclosure policy version");
                    continue;
                }

                if (reference.OwnerModuleId != id
                    && !module.Manifest.Requires.Contains(reference.OwnerModuleId, StringComparer.Ordinal)
                    && !module.Manifest.OptionalDependencies.Contains(reference.OwnerModuleId, StringComparer.Ordinal))
                    Error(id, $"email producer must declare a dependency on source-policy owner '{reference.OwnerModuleId}'");

                bool reciprocalConsent = (policyEntry.Value.AllowedSystemProducers ?? []).Any(binding =>
                    binding?.Producer is { } producer
                    && producer.ModuleId == id
                    && module.Manifest.Execution.Operations.Any(operation =>
                        operation.OperationKey == producer.OperationKey
                        && operation.OperationVersion == producer.OperationVersion
                        && operation.ExecutionKind is "committed-event" or "scheduled-system"
                        && operation.SystemGrantKey == grant.GrantKey
                        && grant.AllowedOperationKeys.Contains(operation.OperationKey, StringComparer.Ordinal)));
                if (!reciprocalConsent)
                    Error(id, $"email grant '{grant.GrantKey}' has no reciprocal source-policy consent for its operations");
            }
        }

        void Error(string id, string message) => errors.Add($"Trykatch module '{id}' {message}.");
    }

    private static void ValidateTemplateReference(string module, ModuleEmailTemplateRefManifest? reference, List<string> errors)
    {
        if (reference is null)
        {
            errors.Add($"Trykatch module '{module}' email template reference is invalid.");
            return;
        }

        Owner(module, reference.OwnerModuleId, errors);
        Key(module, reference.TemplateKey, "email template reference", errors);
        Version(module, reference.TemplateVersion, "email template reference", errors);
        if (PurposeLength(reference.OwnerModuleId, reference.TemplateKey) > MaximumTemplatePurposeLength)
            errors.Add($"Trykatch module '{module}' email template reference exceeds the bounded purpose key length.");
    }

    private static void ValidatePolicyReference(string module, ModuleEmailDisclosurePolicyRefManifest? reference, List<string> errors)
    {
        if (reference is null)
        {
            errors.Add($"Trykatch module '{module}' email disclosure policy reference is invalid.");
            return;
        }
        Owner(module, reference.OwnerModuleId, errors);
        Key(module, reference.PolicyKey, "email disclosure policy reference", errors);
        Version(module, reference.PolicyVersion, "email disclosure policy reference", errors);
    }

    private static void ValidateProducer(string module, ModuleEmailProducerRefManifest? producer, List<string> errors)
    {
        if (producer is null)
        {
            errors.Add($"Trykatch module '{module}' email producer reference is invalid.");
            return;
        }
        Owner(module, producer.ModuleId, errors);
        Key(module, producer.OperationKey, "email producer operation", errors);
        Version(module, producer.OperationVersion, "email producer operation", errors);
    }

    private static void Owner(string module, string? value, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 80 || !ModuleId.IsMatch(value))
            errors.Add($"Trykatch module '{module}' email reference requires a stable module identifier.");
    }

    private static void Key(string module, string? value, string subject, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160 || !StableKey.IsMatch(value))
            errors.Add($"Trykatch module '{module}' {subject} requires a bounded stable lower-case key.");
    }

    private static void Version(string module, int value, string subject, List<string> errors)
    {
        if (value < 1) errors.Add($"Trykatch module '{module}' {subject} requires a positive version.");
    }

    private static int PurposeLength(string? owner, string? key) => (owner?.Length ?? 0) + 1 + (key?.Length ?? 0);
    private static string TemplateKey(ModuleEmailTemplateRefManifest? value) => value is null ? "<null>" : $"{value.OwnerModuleId}/{value.TemplateKey}/{value.TemplateVersion}";
    private static string PolicyKey(ModuleEmailDisclosurePolicyRefManifest? value) => value is null ? "<null>" : $"{value.OwnerModuleId}/{value.PolicyKey}/{value.PolicyVersion}";
    private static string BindingKey(ModuleEmailSystemProducerBindingManifest? value) => value is null ? "<null>" : $"{value.Producer?.ModuleId}/{value.Producer?.OperationKey}/{value.Producer?.OperationVersion}/{TemplateKey(value.Template)}";

    private static void ValidateFinite<T>(IReadOnlyList<T>? values, string module, string subject, List<string> errors,
        bool nonempty = false, Func<T, string>? keySelector = null)
    {
        if (values is null || values.Count > MaximumSetSize || nonempty && values.Count == 0)
        {
            errors.Add($"Trykatch module '{module}' {subject} must be a finite unique set.");
            return;
        }

        if (values.Any(value => value is null)
            || keySelector is not null && values.Select(keySelector).Distinct(StringComparer.Ordinal).Count() != values.Count
            || keySelector is null && values.Distinct().Count() != values.Count)
            errors.Add($"Trykatch module '{module}' {subject} must be a finite unique set.");
    }

    private readonly record struct TemplateIdentity(string OwnerModuleId, string TemplateKey, int TemplateVersion);
    private readonly record struct PolicyIdentity(string OwnerModuleId, string PolicyKey, int PolicyVersion);
}
