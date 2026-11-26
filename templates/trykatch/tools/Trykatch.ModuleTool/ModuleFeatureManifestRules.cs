using System.Text.RegularExpressions;

namespace Trykatch.ModuleTool;

internal static class ModuleFeatureManifestRules
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
            List<ModuleFeatureManifest?>? features = module.Manifest.Features;
            if (features is null)
            {
                errors.Add($"Module '{moduleId}' has a null feature declaration list.");
                continue;
            }

            if (features.Count > MaximumDeclarations)
                errors.Add($"Module '{moduleId}' declares more than {MaximumDeclarations} features.");

            HashSet<string> keys = new(StringComparer.Ordinal);
            foreach (ModuleFeatureManifest? feature in features)
            {
                if (feature is null)
                {
                    errors.Add($"Module '{moduleId}' has a null feature declaration.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(feature.Key)
                    || feature.Key.Length > MaximumKeyLength
                    || !StableKey.IsMatch(feature.Key)
                    || !feature.Key.StartsWith(moduleId + ".", StringComparison.Ordinal))
                    errors.Add($"Module '{moduleId}' declares invalid or foreign-owner feature key '{feature.Key}'.");
                if (!string.IsNullOrWhiteSpace(feature.Key) && !keys.Add(feature.Key))
                    errors.Add($"Module '{moduleId}' declares duplicate feature key '{feature.Key}'.");
                if (string.IsNullOrWhiteSpace(feature.Name) || feature.Name.Length > MaximumNameLength)
                    errors.Add($"Feature '{feature.Key}' in module '{moduleId}' requires a nonblank name of at most {MaximumNameLength} characters.");
                if (string.IsNullOrWhiteSpace(feature.Description) || feature.Description.Length > MaximumDescriptionLength)
                    errors.Add($"Feature '{feature.Key}' in module '{moduleId}' requires a nonblank description of at most {MaximumDescriptionLength} characters.");
            }
        }
    }
}
