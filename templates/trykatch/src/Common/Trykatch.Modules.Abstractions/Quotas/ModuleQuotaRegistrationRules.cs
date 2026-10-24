using System.Text.RegularExpressions;

namespace Trykatch.Modules;

/// <summary>Validates explicit quota resource declarations and generated manifest expectations.</summary>
public static class ModuleQuotaRegistrationRules
{
    private static readonly Regex StableModuleId = new("^[a-z][a-z0-9]*(?:-[a-z0-9]+)*\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex StableKey = new("^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static void Validate(IReadOnlyCollection<ModuleDescriptor> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        HashSet<string> owners = new(StringComparer.Ordinal);
        foreach (ModuleDescriptor module in modules)
        {
            if (module is null || string.IsNullOrWhiteSpace(module.Id) || module.Id.Length > 80
                || !StableModuleId.IsMatch(module.Id) || !owners.Add(module.Id))
                throw Invalid("Quota declarations require unique valid module identifiers.");
            if (module.QuotaResources is null || module.QuotaResources.Count > 100)
                throw Invalid($"Module '{module.Id}' requires a quota resource collection of at most 100 declarations.");

            HashSet<string> keys = new(StringComparer.Ordinal);
            HashSet<(string Key, int Version)> uploadPolicies = [];
            HashSet<(string Key, int Version)> inventorySources = [];
            HashSet<(string Key, int Version)> removalPolicies = [];
            foreach (ModuleQuotaResourceDescriptor resource in module.QuotaResources)
            {
                ValidateResource(module.Id, resource, module.Permissions);
                if (!keys.Add(resource.Key))
                    throw Invalid($"Module '{module.Id}' declares a duplicate quota resource key.");
                if (resource.UploadPolicy is { } upload
                    && !uploadPolicies.Add((upload.PolicyKey, upload.PolicyVersion)))
                    throw Invalid($"Module '{module.Id}' declares a duplicate upload policy identity.");
                if (resource.InventorySource is { } inventory
                    && !inventorySources.Add((inventory.SourceKey, inventory.SourceVersion)))
                    throw Invalid($"Module '{module.Id}' declares a duplicate quota inventory source identity.");
                if (resource.RemovalPolicy is { } removal
                    && !removalPolicies.Add((removal.PolicyKey, removal.PolicyVersion)))
                    throw Invalid($"Module '{module.Id}' declares a duplicate usage removal policy identity.");
            }

            foreach (ModuleSystemGrantDescriptor grant in module.SystemGrants)
            {
                if (grant.AllowedQuotaResources is null || grant.AllowedQuotaResources.Count > 100)
                    throw Invalid($"Module '{module.Id}' requires a finite quota resource set for each system grant.");
                HashSet<string> allowed = new(StringComparer.Ordinal);
                foreach (string resourceKey in grant.AllowedQuotaResources)
                {
                    if (string.IsNullOrWhiteSpace(resourceKey) || !keys.Contains(resourceKey)
                        || !allowed.Add(resourceKey))
                        throw Invalid($"Module '{module.Id}' has a system grant with an undeclared or duplicate quota resource.");
                }
            }
        }
    }

    public static void ValidateManifest(
        IReadOnlyCollection<IModule> modules,
        IReadOnlyCollection<InstalledModuleQuotaResource> expected)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(expected);
        ModuleDescriptor[] descriptors = modules
            .Select(module => module?.Descriptor ?? throw Invalid("Quota expectations require installed module descriptors."))
            .ToArray();
        Validate(descriptors);

        HashSet<string> owners = descriptors.Select(module => module.Id).ToHashSet(StringComparer.Ordinal);
        Dictionary<string, (string ModuleId, ModuleQuotaResourceDescriptor Resource)> actualByKey = descriptors
            .SelectMany(module => module.QuotaResources.Select(resource => (ModuleId: module.Id, Resource: resource)))
            .ToDictionary(entry => entry.Resource.Key, entry => entry, StringComparer.Ordinal);
        Dictionary<string, (string ModuleId, ModuleQuotaResourceDescriptor Resource)> expectedByKey = new(StringComparer.Ordinal);
        Dictionary<string, ModuleDescriptor> descriptorsByOwner = descriptors.ToDictionary(module => module.Id,
            StringComparer.Ordinal);

        foreach (InstalledModuleQuotaResource entry in expected)
        {
            if (entry is null || entry.ModuleId is null || !owners.Contains(entry.ModuleId))
                throw Invalid("Quota expectations require a known installed module owner.");
            ValidateResource(entry.ModuleId, entry.Resource, descriptorsByOwner[entry.ModuleId].Permissions);
            if (!expectedByKey.TryAdd(entry.Resource.Key, (entry.ModuleId, entry.Resource)))
                throw Invalid("Quota expectations contain a duplicate key.");
        }

        if (actualByKey.Count != expectedByKey.Count || actualByKey.Any(pair =>
                !expectedByKey.TryGetValue(pair.Key, out var expectation)
                || expectation.ModuleId != pair.Value.ModuleId
                || expectation.Resource != pair.Value.Resource))
            throw Invalid("Installed quota resource declarations do not match the generated manifest expectations.");
    }

    private static void ValidateResource(string moduleId, ModuleQuotaResourceDescriptor resource,
        IReadOnlyList<PermissionDescriptor>? permissions)
    {
        if (resource is null)
            throw Invalid($"Module '{moduleId}' declares a null quota resource.");
        if (string.IsNullOrWhiteSpace(resource.Key) || resource.Key.Length > 120 || !StableKey.IsMatch(resource.Key)
            || !resource.Key.StartsWith(moduleId + ".", StringComparison.Ordinal))
            throw Invalid($"Module '{moduleId}' requires a stable owned quota resource key of at most 120 characters.");
        if (string.IsNullOrWhiteSpace(resource.Name) || resource.Name.Length > 160)
            throw Invalid($"Module '{moduleId}' requires a nonblank quota resource name of at most 160 characters.");
        if (string.IsNullOrWhiteSpace(resource.Description) || resource.Description.Length > 1000)
            throw Invalid($"Module '{moduleId}' requires a nonblank quota resource description of at most 1000 characters.");
        if (resource.Unit is not ModuleQuotaUnit.Bytes and not ModuleQuotaUnit.Count)
            throw Invalid($"Module '{moduleId}' declares an unknown quota resource unit.");

        if (resource.UploadPolicy is { } upload)
        {
            ValidateOwnedReference(moduleId, upload.OwnerModuleId, upload.PolicyKey, upload.PolicyVersion,
                "upload policy");
            if (resource.Unit != ModuleQuotaUnit.Bytes)
                throw Invalid($"Module '{moduleId}' upload policies require a byte quota resource.");
            if (string.IsNullOrWhiteSpace(upload.RequiredPermission)
                || !IsStableOwnedKey(moduleId, upload.RequiredPermission)
                || permissions?.Any(permission => permission is not null
                    && string.Equals(permission.Key, upload.RequiredPermission, StringComparison.Ordinal)) != true)
                throw Invalid($"Module '{moduleId}' upload policies require a declared module-owned permission.");
        }

        if (resource.InventorySource is { } inventory)
            ValidateOwnedReference(moduleId, inventory.OwnerModuleId, inventory.SourceKey, inventory.SourceVersion,
                "quota inventory source");
        if (resource.RemovalPolicy is { } removal)
            ValidateOwnedReference(moduleId, removal.OwnerModuleId, removal.PolicyKey, removal.PolicyVersion,
                "usage removal policy");
    }

    private static void ValidateOwnedReference(string moduleId, string? ownerModuleId, string? key, int version,
        string description)
    {
        if (!string.Equals(ownerModuleId, moduleId, StringComparison.Ordinal)
            || !IsStableOwnedKey(moduleId, key) || version <= 0)
            throw Invalid($"Module '{moduleId}' requires a valid owned {description} identity and positive version.");
    }

    private static bool IsStableOwnedKey(string moduleId, string? key) => key is { Length: <= 120 }
        && StableKey.IsMatch(key) && key.StartsWith(moduleId + ".", StringComparison.Ordinal);

    private static InvalidOperationException Invalid(string message) => new(message);
}
