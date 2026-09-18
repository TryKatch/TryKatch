namespace Trykatch.Modules;

/// <summary>Request-scoped organization opt-in; never a provider-selection seam.</summary>
public interface IOrganizationAssistantActivation
{
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken);
    Task<Guid> VersionAsync(CancellationToken cancellationToken);
}
