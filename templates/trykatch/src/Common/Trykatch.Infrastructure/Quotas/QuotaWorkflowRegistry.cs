using Trykatch.Modules;

namespace Trykatch.Infrastructure.Quotas;

/// <summary>Resolves only quota workflows declared by installed modules.</summary>
internal sealed class QuotaWorkflowRegistry
{
    private readonly Dictionary<ModuleUploadPolicyRef, InstalledModuleQuotaResource> uploads = [];
    private readonly Dictionary<string, ModuleQuotaInventorySourceBinding> inventories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ModuleUsageRemovalPolicyBinding> removals = new(StringComparer.Ordinal);

    public QuotaWorkflowRegistry(
        ModuleCatalog catalog,
        IEnumerable<ModuleQuotaInventorySourceBinding> inventoryBindings,
        IEnumerable<ModuleUsageRemovalPolicyBinding> removalBindings)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(inventoryBindings);
        ArgumentNullException.ThrowIfNull(removalBindings);

        ModuleDescriptor[] modules = catalog.Descriptors.ToArray();
        foreach (ModuleDescriptor module in modules)
        foreach (ModuleQuotaResourceDescriptor resource in module.QuotaResources)
        {
            InstalledModuleQuotaResource installed = new(module.Id, resource);
            if (resource.UploadPolicy is { } upload)
            {
                ModuleUploadPolicyRef policy = new(upload.OwnerModuleId, upload.PolicyKey, upload.PolicyVersion);
                if (!uploads.TryAdd(policy, installed))
                    throw Invalid("Installed modules declare an ambiguous upload policy identity.");
            }

        }

        BindInventory(modules, inventoryBindings);
        BindRemoval(modules, removalBindings);
    }

    public bool TryGetUpload(ModuleUploadPolicyRef policy, out InstalledModuleQuotaResource? resource)
    {
        if (policy is not null && uploads.TryGetValue(policy, out InstalledModuleQuotaResource? found))
        {
            resource = found;
            return true;
        }

        resource = null;
        return false;
    }

    public bool TryGetInventory(string resourceKey, out ModuleQuotaInventorySourceBinding? binding)
    {
        if (resourceKey is not null && inventories.TryGetValue(resourceKey, out ModuleQuotaInventorySourceBinding? found))
        {
            binding = found;
            return true;
        }

        binding = null;
        return false;
    }

    public bool TryGetRemoval(string resourceKey, out ModuleUsageRemovalPolicyBinding? binding)
    {
        if (resourceKey is not null && removals.TryGetValue(resourceKey, out ModuleUsageRemovalPolicyBinding? found))
        {
            binding = found;
            return true;
        }

        binding = null;
        return false;
    }

    private void BindInventory(
        IReadOnlyCollection<ModuleDescriptor> modules,
        IEnumerable<ModuleQuotaInventorySourceBinding> suppliedBindings)
    {
        Dictionary<ModuleQuotaInventorySourceRef, string> expected = [];
        foreach (ModuleDescriptor module in modules)
        foreach (ModuleQuotaResourceDescriptor resource in module.QuotaResources)
        {
            if (resource.InventorySource is not { } declared)
                continue;
            if (!expected.TryAdd(declared, resource.Key))
                throw Invalid("Installed modules declare an ambiguous quota inventory source identity.");
        }

        HashSet<ModuleQuotaInventorySourceRef> seen = [];
        foreach (ModuleQuotaInventorySourceBinding binding in suppliedBindings)
        {
            if (binding?.Source is not { } source || binding.Adapter is null
                || !expected.TryGetValue(source, out string? resourceKey)
                || !seen.Add(source))
                throw Invalid("Quota inventory source bindings must exactly match installed declarations.");
            inventories.Add(resourceKey, binding);
        }

        if (seen.Count != expected.Count)
            throw Invalid("Every installed quota inventory source requires exactly one adapter binding.");
    }

    private void BindRemoval(
        IReadOnlyCollection<ModuleDescriptor> modules,
        IEnumerable<ModuleUsageRemovalPolicyBinding> suppliedBindings)
    {
        Dictionary<ModuleUsageRemovalPolicyRef, string> expected = [];
        foreach (ModuleDescriptor module in modules)
        foreach (ModuleQuotaResourceDescriptor resource in module.QuotaResources)
        {
            if (resource.RemovalPolicy is not { } declared)
                continue;
            if (!expected.TryAdd(declared, resource.Key))
                throw Invalid("Installed modules declare an ambiguous usage removal policy identity.");
        }

        HashSet<ModuleUsageRemovalPolicyRef> seen = [];
        foreach (ModuleUsageRemovalPolicyBinding binding in suppliedBindings)
        {
            if (binding?.Policy is not { } policy || binding.Adapter is null
                || !expected.TryGetValue(policy, out string? resourceKey)
                || !seen.Add(policy))
                throw Invalid("Usage removal policy bindings must exactly match installed declarations.");
            removals.Add(resourceKey, binding);
        }

        if (seen.Count != expected.Count)
            throw Invalid("Every installed usage removal policy requires exactly one adapter binding.");
    }

    private static InvalidOperationException Invalid(string message) => new(message);
}
