using System.Text.RegularExpressions;

namespace Trykatch.Modules;

/// <summary>Validates explicit feature declarations and generated manifest expectations.</summary>
public static class ModuleFeatureRegistrationRules
{
    private static readonly Regex StableModuleId = new("^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex StableKey = new("^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static void Validate(IReadOnlyCollection<ModuleDescriptor> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        HashSet<string> owners = new(StringComparer.Ordinal);
        foreach (ModuleDescriptor module in modules)
        {
            if (module is null || string.IsNullOrWhiteSpace(module.Id) || module.Id.Length > 80
                || !StableModuleId.IsMatch(module.Id) || !owners.Add(module.Id))
                throw Invalid("Feature declarations require unique valid module identifiers.");
            if (module.Features is null || module.Features.Count > 100)
                throw Invalid($"Module '{module.Id}' requires a feature collection of at most 100 declarations.");
            HashSet<string> keys = new(StringComparer.Ordinal);
            foreach (ModuleFeatureDescriptor feature in module.Features)
            {
                ValidateFeature(module.Id, feature);
                if (!keys.Add(feature.Key))
                    throw Invalid($"Module '{module.Id}' declares a duplicate feature key.");
            }
        }
    }

    public static void ValidateManifest(IReadOnlyCollection<IModule> modules, IReadOnlyCollection<InstalledModuleFeature> expected)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(expected);
        ModuleDescriptor[] descriptors = modules
            .Select(module => module?.Descriptor ?? throw Invalid("Feature expectations require installed module descriptors."))
            .ToArray();
        Validate(descriptors);

        HashSet<string> owners = descriptors.Select(module => module.Id).ToHashSet(StringComparer.Ordinal);
        Dictionary<string, ModuleFeatureDescriptor> actualByKey = descriptors
            .SelectMany(module => module.Features)
            .ToDictionary(feature => feature.Key, StringComparer.Ordinal);
        Dictionary<string, ModuleFeatureDescriptor> expectedByKey = new(StringComparer.Ordinal);
        foreach (InstalledModuleFeature entry in expected)
        {
            if (entry is null || entry.ModuleId is null || !owners.Contains(entry.ModuleId))
                throw Invalid("Feature expectations require a known installed module owner.");
            ValidateFeature(entry.ModuleId, entry.Feature);
            if (!expectedByKey.TryAdd(entry.Feature.Key, entry.Feature))
                throw Invalid("Feature expectations contain a duplicate key.");
        }

        if (actualByKey.Count != expectedByKey.Count || actualByKey.Any(pair =>
                !expectedByKey.TryGetValue(pair.Key, out ModuleFeatureDescriptor? feature) || feature != pair.Value))
            throw Invalid("Installed feature declarations do not match the generated manifest expectations.");
    }

    private static void ValidateFeature(string moduleId, ModuleFeatureDescriptor feature)
    {
        if (feature is null)
            throw Invalid($"Module '{moduleId}' declares a null feature.");
        if (string.IsNullOrWhiteSpace(feature.Key) || feature.Key.Length > 120 || !StableKey.IsMatch(feature.Key)
            || !feature.Key.StartsWith(moduleId + ".", StringComparison.Ordinal))
            throw Invalid($"Module '{moduleId}' requires a stable owned feature key of at most 120 characters.");
        if (string.IsNullOrWhiteSpace(feature.Name) || feature.Name.Length > 160)
            throw Invalid($"Module '{moduleId}' requires a nonblank feature name of at most 160 characters.");
        if (string.IsNullOrWhiteSpace(feature.Description) || feature.Description.Length > 1000)
            throw Invalid($"Module '{moduleId}' requires a nonblank feature description of at most 1000 characters.");
    }

    private static InvalidOperationException Invalid(string message) => new(message);
}
