using System.Text.RegularExpressions;

namespace Trykatch.ModuleTool;

internal static class ModuleQuotaManifestRules
{
    private const int MaximumDeclarations = 100;
    private const int MaximumKeyLength = 120;
    private const int MaximumNameLength = 160;
    private const int MaximumDescriptionLength = 1000;
    private static readonly Regex StableKey = new(
        "^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static void Validate(
        IReadOnlyCollection<ModuleWorkspace.LoadedModule> modules,
        List<string> errors)
    {
        foreach (ModuleWorkspace.LoadedModule module in modules)
        {
            string moduleId = module.Manifest.Id;
            List<ModuleQuotaResourceManifest?>? resources = module.Manifest.QuotaResources;
            if (resources is null)
            {
                errors.Add($"Module '{moduleId}' has a null quota resource declaration list.");
                continue;
            }

            if (resources.Count > MaximumDeclarations)
                errors.Add($"Module '{moduleId}' declares more than {MaximumDeclarations} quota resources.");

            HashSet<string> keys = new(StringComparer.Ordinal);
            HashSet<(string Key, int Version)> uploads = [];
            HashSet<(string Key, int Version)> sources = [];
            HashSet<(string Key, int Version)> removals = [];
            foreach (ModuleQuotaResourceManifest? resource in resources)
            {
                if (resource is null)
                {
                    errors.Add($"Module '{moduleId}' has a null quota resource declaration.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(resource.Key)
                    || resource.Key.Length > MaximumKeyLength
                    || !StableKey.IsMatch(resource.Key)
                    || !resource.Key.StartsWith(moduleId + ".", StringComparison.Ordinal))
                    errors.Add($"Module '{moduleId}' declares invalid or foreign-owner quota resource key '{resource.Key}'.");
                if (!string.IsNullOrWhiteSpace(resource.Key) && !keys.Add(resource.Key))
                    errors.Add($"Module '{moduleId}' declares duplicate quota resource key '{resource.Key}'.");
                if (string.IsNullOrWhiteSpace(resource.Name) || resource.Name.Length > MaximumNameLength)
                    errors.Add($"Quota resource '{resource.Key}' in module '{moduleId}' requires a nonblank name of at most {MaximumNameLength} characters.");
                if (string.IsNullOrWhiteSpace(resource.Description) || resource.Description.Length > MaximumDescriptionLength)
                    errors.Add($"Quota resource '{resource.Key}' in module '{moduleId}' requires a nonblank description of at most {MaximumDescriptionLength} characters.");
                if (resource.Unit is not "bytes" and not "count")
                    errors.Add($"Quota resource '{resource.Key}' in module '{moduleId}' declares unknown unit '{resource.Unit}'. Expected 'bytes' or 'count'.");
                if (resource.UploadPolicy is { } upload)
                {
                    ValidateReference(moduleId, upload.PolicyKey, upload.PolicyVersion, "upload policy", uploads, errors);
                    if (resource.Unit != "bytes") errors.Add($"Quota upload policy in module '{moduleId}' requires a byte resource.");
                    if (!IsOwnedKey(moduleId, upload.RequiredPermission)
                        || !module.Manifest.Contributions.Permissions.Contains(upload.RequiredPermission, StringComparer.Ordinal))
                        errors.Add($"Quota upload policy in module '{moduleId}' requires a declared module-owned permission.");
                }
                if (resource.InventorySource is { } source)
                    ValidateReference(moduleId, source.SourceKey, source.SourceVersion, "inventory source", sources, errors);
                if (resource.RemovalPolicy is { } removal)
                    ValidateReference(moduleId, removal.PolicyKey, removal.PolicyVersion, "removal policy", removals, errors);
            }

            foreach (ModuleSystemGrantManifest grant in module.Manifest.Execution.SystemGrants)
            {
                if (grant.AllowedQuotaResources is null || grant.AllowedQuotaResources.Count > MaximumDeclarations)
                {
                    errors.Add($"System grant '{grant.GrantKey}' in module '{moduleId}' requires a finite quota resource set.");
                    continue;
                }
                HashSet<string> allowed = new(StringComparer.Ordinal);
                foreach (string? key in grant.AllowedQuotaResources)
                {
                    if (string.IsNullOrWhiteSpace(key) || !keys.Contains(key) || !allowed.Add(key))
                        errors.Add($"System grant '{grant.GrantKey}' in module '{moduleId}' declares an undeclared or duplicate quota resource.");
                }
            }
        }
    }

    private static void ValidateReference(string owner, string key, int version, string kind,
        HashSet<(string Key, int Version)> identities, List<string> errors)
    {
        if (!IsOwnedKey(owner, key) || version <= 0)
            errors.Add($"Module '{owner}' declares an invalid owned quota {kind} identity/version.");
        if (!identities.Add((key, version))) errors.Add($"Module '{owner}' declares a duplicate quota {kind} identity.");
    }

    private static bool IsOwnedKey(string owner, string? key) => key is { Length: <= MaximumKeyLength }
        && StableKey.IsMatch(key) && key.StartsWith(owner + ".", StringComparison.Ordinal);
}
