namespace Trykatch.Modules;

/// <summary>Neutral host seam for checking an installed feature in the current organization.</summary>
public interface IOrganizationFeatureEvaluator
{
    Task<bool> IsEnabledAsync(string featureKey, CancellationToken ct);
}

/// <summary>Static declaration of a module feature; it does not grant permissions or access.</summary>
public sealed record ModuleFeatureDescriptor(
    string Key,
    string Name,
    string Description,
    bool DefaultEnabled);

/// <summary>One explicit manifest expectation emitted by the module generator.</summary>
public sealed record InstalledModuleFeature(string ModuleId, ModuleFeatureDescriptor Feature);
