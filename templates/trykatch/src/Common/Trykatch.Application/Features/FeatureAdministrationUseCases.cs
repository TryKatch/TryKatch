using Trykatch.Application.Common;
using Trykatch.Application.Identity;
using Trykatch.Application.Organizations;
using Trykatch.Modules;

namespace Trykatch.Application.Features;

/// <summary>Strict application boundary for platform feature controls.</summary>
public sealed class FeatureAdministration(
    IPlatformAuthorityReader authority,
    IFeatureControlStore store,
    IOrganizationDirectory organizations,
    ModuleCatalog modules,
    FeaturePolicySettings settings)
{
    private const int MaximumPage = 10_000;
    private const int MaximumPageSize = 100;

    public async Task<Result<FeatureAdministrationSnapshot>> ListAsync(Guid actorId, Guid? organizationId, CancellationToken ct)
    {
        if (!await IsAuthorizedAsync(actorId, PlatformPermissions.FeaturesRead, ct))
            return Denied<FeatureAdministrationSnapshot>();
        if (organizationId == Guid.Empty)
            return Invalid<FeatureAdministrationSnapshot>();
        if (organizationId is Guid requestedOrganization && !await OrganizationExistsAsync(requestedOrganization, ct))
            return NotFound<FeatureAdministrationSnapshot>();

        List<FeatureAdministrationItem> items = [];
        foreach (ModuleDescriptor module in modules.Descriptors.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            foreach (ModuleFeatureDescriptor declaration in module.Features.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                var target = new FeatureControlTarget(declaration.Key);
                FeatureControlState environmentControl = await store.ReadAsync(actorId, settings.Environment, target, ct);
                FeatureControlState? organizationControl = organizationId is Guid id
                    ? await store.ReadAsync(actorId, settings.Environment, target with { OrganizationId = id }, ct)
                    : null;
                bool? configuredDefault = settings.Defaults.TryGetValue(declaration.Key, out bool configured)
                    ? configured
                    : null;
                items.Add(new(module.Id, declaration, settings.Disabled.Contains(declaration.Key), configuredDefault,
                    environmentControl, organizationControl));
            }
        }

        return Result.Success(new FeatureAdministrationSnapshot(settings.Environment, organizationId, items));
    }

    public async Task<Result<PagedResult<FeatureControlHistoryEntry>>> HistoryAsync(Guid actorId,
        FeatureControlTarget target, int page, int pageSize, CancellationToken ct)
    {
        if (!await IsAuthorizedAsync(actorId, PlatformPermissions.FeaturesRead, ct))
            return Denied<PagedResult<FeatureControlHistoryEntry>>();
        if (!IsValidTarget(target))
            return Invalid<PagedResult<FeatureControlHistoryEntry>>();
        if (!IsInstalledFeature(target.FeatureKey))
            return NotFound<PagedResult<FeatureControlHistoryEntry>>();
        if (target.OrganizationId is Guid organizationId && !await OrganizationExistsAsync(organizationId, ct))
            return NotFound<PagedResult<FeatureControlHistoryEntry>>();

        int boundedPage = Math.Clamp(page, 1, MaximumPage);
        int boundedPageSize = Math.Clamp(pageSize, 1, MaximumPageSize);
        PagedResult<FeatureControlHistoryEntry> history = await store.HistoryAsync(actorId, settings.Environment,
            target, boundedPage, boundedPageSize, ct);
        return Result.Success(history);
    }

    public async Task<Result<FeatureControlState>> SaveEnvironmentAsync(Guid actorId, string featureKey,
        SaveEnvironmentFeatureControl command, CancellationToken ct)
    {
        if (!await IsAuthorizedAsync(actorId, PlatformPermissions.FeaturesManage, ct))
            return Denied<FeatureControlState>();
        if (command is null)
            return Invalid<FeatureControlState>();
        if (!IsInstalledFeature(featureKey))
            return NotFound<FeatureControlState>();

        var target = new FeatureControlTarget(featureKey);
        if (!await store.TryLockAsync(actorId, settings.Environment, target, ct))
            return Busy<FeatureControlState>();
        if (!await IsAuthorizedAsync(actorId, PlatformPermissions.FeaturesManage, ct))
            return Denied<FeatureControlState>();

        FeatureControlSaveResult saved = await store.SaveEnvironmentAsync(actorId, settings.Environment, featureKey, command, ct);
        return ToSaveResult(saved);
    }

    public async Task<Result<FeatureControlState>> SaveOrganizationAsync(Guid actorId, FeatureControlTarget target,
        SaveOrganizationFeatureOverride command, CancellationToken ct)
    {
        if (!await IsAuthorizedAsync(actorId, PlatformPermissions.FeaturesManage, ct))
            return Denied<FeatureControlState>();
        if (command is null || !IsValidTarget(target) || target.OrganizationId is null)
            return Invalid<FeatureControlState>();
        if (!IsInstalledFeature(target.FeatureKey))
            return NotFound<FeatureControlState>();

        Guid organizationId = target.OrganizationId.Value;
        if (!await OrganizationExistsAsync(organizationId, ct))
            return NotFound<FeatureControlState>();
        if (!await store.TryLockAsync(actorId, settings.Environment, target, ct))
            return Busy<FeatureControlState>();
        if (!await IsAuthorizedAsync(actorId, PlatformPermissions.FeaturesManage, ct))
            return Denied<FeatureControlState>();

        FeatureControlSaveResult saved = await store.SaveOrganizationAsync(actorId, settings.Environment, target, command, ct);
        return ToSaveResult(saved);
    }

    private async Task<bool> IsAuthorizedAsync(Guid actorId, string permission, CancellationToken ct)
    {
        if (actorId == Guid.Empty)
            return false;
        EffectivePlatformAccess current = await authority.ReadAsync(actorId, ct);
        return current.HasPermission(permission);
    }

    private async Task<bool> OrganizationExistsAsync(Guid organizationId, CancellationToken ct) =>
        await organizations.FindAsync(organizationId, ct) is not null;

    private bool IsInstalledFeature(string featureKey) =>
        modules.Descriptors.Any(module => module.Features.Any(feature =>
            string.Equals(feature.Key, featureKey, StringComparison.Ordinal)));

    private static bool IsValidTarget(FeatureControlTarget? target) => target is not null
        && target.OrganizationId != Guid.Empty
        && !string.IsNullOrWhiteSpace(target.FeatureKey);

    private static Result<FeatureControlState> ToSaveResult(FeatureControlSaveResult saved) =>
        saved.Status == FeatureControlSaveStatus.Saved && saved.State is not null
            ? Result.Success(saved.State)
            : Result.Failure<FeatureControlState>("feature_control_conflict", "The feature control changed. Reload it and try again.");

    private static Result<T> Denied<T>() =>
        Result.Failure<T>("forbidden", "Current platform authority does not allow this feature operation.");

    private static Result<T> Invalid<T>() =>
        Result.Failure<T>("invalid_request", "The feature control target or command is invalid.");

    private static Result<T> NotFound<T>() =>
        Result.Failure<T>("not_found", "The requested feature or organization was not found.");

    private static Result<T> Busy<T>() =>
        Result.Failure<T>("feature_control_busy", "Another feature control update is in progress. Try again later.");
}
