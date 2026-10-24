using Trykatch.Application.Common;

namespace Trykatch.Application.Features;

public sealed record FeatureControlTarget(string FeatureKey, Guid? OrganizationId = null);
public sealed record SaveEnvironmentFeatureControl(bool? DefaultEnabled, bool ForceDisabled, Guid ExpectedVersion);
public sealed record SaveOrganizationFeatureOverride(bool? Enabled, Guid ExpectedVersion);

/// <summary>Enabled is the nullable environment default or organization override for this target.</summary>
public sealed record FeatureControlState(FeatureControlTarget Target, bool? Enabled, bool ForceDisabled,
    Guid Version, DateTimeOffset? UpdatedAtUtc);

public enum FeatureControlSaveStatus { Saved, Conflict }
public sealed record FeatureControlSaveResult(FeatureControlSaveStatus Status, FeatureControlState? State = null);

public sealed record FeatureControlHistoryEntry(Guid Id, FeatureControlTarget Target, Guid ActorId,
    Guid? PreviousVersion, Guid Version, bool? PreviousEnabled, bool? Enabled,
    bool? PreviousForceDisabled, bool? ForceDisabled, DateTimeOffset ChangedAtUtc);

/// <summary>Platform-profile persistence within the caller's transaction; implementations never commit it.</summary>
public interface IFeatureControlStore
{
    Task<FeatureControlState> ReadAsync(Guid actorId, string environment, FeatureControlTarget target, CancellationToken ct);
    Task<bool> TryLockAsync(Guid actorId, string environment, FeatureControlTarget target, CancellationToken ct);
    Task<FeatureControlSaveResult> SaveEnvironmentAsync(Guid actorId, string environment, string featureKey,
        SaveEnvironmentFeatureControl command, CancellationToken ct);
    Task<FeatureControlSaveResult> SaveOrganizationAsync(Guid actorId, string environment, FeatureControlTarget target,
        SaveOrganizationFeatureOverride command, CancellationToken ct);
    Task<PagedResult<FeatureControlHistoryEntry>> HistoryAsync(Guid actorId, string environment, FeatureControlTarget target,
        int page, int pageSize, CancellationToken ct);
}
