using System.Buffers.Binary;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Trykatch.Application.Common;
using Trykatch.Application.Features;
using Trykatch.Infrastructure.Persistence;

namespace Trykatch.Infrastructure.Features;

/// <summary>Platform control persistence; the caller owns authorization and transaction completion.</summary>
internal sealed class FeatureControlStore : IFeatureControlStore
{
    private static readonly Regex EnvironmentPattern = new("^[a-z0-9][a-z0-9._-]{0,63}\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex FeaturePattern = new("^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly PlatformDbContext context;
    private readonly TimeProvider clock;
    private bool attempted;
    private AcquiredTarget? acquired;

    internal FeatureControlStore(PlatformDbContext context, TimeProvider clock)
    {
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<FeatureControlState> ReadAsync(Guid actorId, string environment, FeatureControlTarget target, CancellationToken ct)
    {
        ValidateTarget(environment, target);
        await RequireScopeAsync(actorId, ct);
        return await ReadCoreAsync(environment, target, ct);
    }

    public async Task<bool> TryLockAsync(Guid actorId, string environment, FeatureControlTarget target, CancellationToken ct)
    {
        ValidateTarget(environment, target);
        if (attempted) throw new InvalidOperationException("A scoped feature store accepts one lock attempt.");
        attempted = true;
        DbTransaction transaction = await RequireScopeAsync(actorId, ct);
        string identity = $"trykatch.feature-control.v1/{environment}/{target.FeatureKey}/{target.OrganizationId?.ToString("D") ?? "environment"}";
        long key = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        bool locked = await context.Database.SqlQuery<bool>(
            $"SELECT pg_try_advisory_xact_lock({key}) AS \"Value\"").SingleAsync(ct);
        if (locked) acquired = new(transaction, actorId, environment, target);
        return locked;
    }

    public Task<FeatureControlSaveResult> SaveEnvironmentAsync(Guid actorId, string environment, string featureKey,
        SaveEnvironmentFeatureControl command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        return SaveAsync(actorId, environment, new(featureKey), command.DefaultEnabled, command.ForceDisabled, command.ExpectedVersion, ct);
    }

    public Task<FeatureControlSaveResult> SaveOrganizationAsync(Guid actorId, string environment, FeatureControlTarget target,
        SaveOrganizationFeatureOverride command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(target);
        if (target.OrganizationId is null) throw new ArgumentException("An organization override requires an organization target.", nameof(target));
        return SaveAsync(actorId, environment, target, command.Enabled, null, command.ExpectedVersion, ct);
    }

    public async Task<PagedResult<FeatureControlHistoryEntry>> HistoryAsync(Guid actorId, string environment,
        FeatureControlTarget target, int page, int pageSize, CancellationToken ct)
    {
        ValidateTarget(environment, target);
        await RequireScopeAsync(actorId, ct);
        int boundedPage = Math.Clamp(page, 1, 10_000), boundedSize = Math.Clamp(pageSize, 1, 100);
        IQueryable<FeatureControlHistory> query = context.Set<FeatureControlHistory>().AsNoTracking()
            .Where(row => row.Environment == environment && row.FeatureKey == target.FeatureKey && row.OrganizationId == target.OrganizationId);
        long count = await query.LongCountAsync(ct);
        FeatureControlHistoryEntry[] rows = await query.OrderByDescending(row => row.ChangedAtUtc).ThenByDescending(row => row.Id)
            .Skip((boundedPage - 1) * boundedSize).Take(boundedSize)
            .Select(row => new FeatureControlHistoryEntry(row.Id, new(row.FeatureKey, row.OrganizationId), row.ActorId,
                row.PreviousVersion, row.Version, row.PreviousEnabled, row.Enabled, row.PreviousForceDisabled,
                row.ForceDisabled, row.ChangedAtUtc)).ToArrayAsync(ct);
        return new(rows, boundedPage, boundedSize, count);
    }

    private async Task<FeatureControlSaveResult> SaveAsync(Guid actorId, string environment, FeatureControlTarget target,
        bool? enabled, bool? forceDisabled, Guid expectedVersion, CancellationToken ct)
    {
        ValidateTarget(environment, target);
        DbTransaction transaction = await RequireScopeAsync(actorId, ct);
        AcquiredTarget locked = acquired ?? throw new InvalidOperationException("A feature save requires its acquired target lock.");
        if (!ReferenceEquals(locked.Transaction, transaction) || locked.ActorId != actorId
            || locked.Environment != environment || locked.Target != target)
            throw new InvalidOperationException("The feature lock belongs to a different target, actor or transaction.");
        // A failed or conflicting attempt cannot be retried inside this scoped store.
        acquired = null;
        FeatureControlState before = await ReadCoreAsync(environment, target, ct);
        if (before.Version != expectedVersion) return new(FeatureControlSaveStatus.Conflict);

        Guid version = Guid.NewGuid();
        DateTimeOffset instant = clock.GetUtcNow();
        DateTimeOffset now = new(instant.UtcTicks / 10 * 10, TimeSpan.Zero);
        int affected;
        if (target.OrganizationId is Guid organizationId)
        {
            affected = expectedVersion == Guid.Empty
                ? await context.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO platform.organization_feature_overrides
                      ("OrganizationId", "Environment", "FeatureKey", "Enabled", "Version", "UpdatedAtUtc")
                    VALUES ({organizationId}, {environment}, {target.FeatureKey}, {enabled}, {version}, {now})
                    ON CONFLICT ("OrganizationId", "Environment", "FeatureKey") DO NOTHING
                    """, ct)
                : await context.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE platform.organization_feature_overrides
                    SET "Enabled" = {enabled}, "Version" = {version}, "UpdatedAtUtc" = {now}
                    WHERE "OrganizationId" = {organizationId} AND "Environment" = {environment}
                      AND "FeatureKey" = {target.FeatureKey} AND "Version" = {expectedVersion}
                    """, ct);
        }
        else
        {
            affected = expectedVersion == Guid.Empty
                ? await context.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO platform.feature_environment_controls
                      ("Environment", "FeatureKey", "DefaultEnabled", "ForceDisabled", "Version", "UpdatedAtUtc")
                    VALUES ({environment}, {target.FeatureKey}, {enabled}, {forceDisabled}, {version}, {now})
                    ON CONFLICT ("Environment", "FeatureKey") DO NOTHING
                    """, ct)
                : await context.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE platform.feature_environment_controls
                    SET "DefaultEnabled" = {enabled}, "ForceDisabled" = {forceDisabled}, "Version" = {version}, "UpdatedAtUtc" = {now}
                    WHERE "Environment" = {environment} AND "FeatureKey" = {target.FeatureKey} AND "Version" = {expectedVersion}
                    """, ct);
        }
        if (affected != 1) return new(FeatureControlSaveStatus.Conflict);

        Guid id = Guid.NewGuid();
        Guid? previousVersion = before.Version == Guid.Empty ? null : before.Version;
        bool? previousForce = target.OrganizationId is null && previousVersion is not null ? before.ForceDisabled : null;
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO platform.feature_control_history
              ("Id", "Environment", "FeatureKey", "OrganizationId", "ActorId", "PreviousVersion", "Version",
               "PreviousEnabled", "Enabled", "PreviousForceDisabled", "ForceDisabled", "ChangedAtUtc")
            VALUES ({id}, {environment}, {target.FeatureKey}, {target.OrganizationId}, {actorId}, {previousVersion}, {version},
                    {before.Enabled}, {enabled}, {previousForce}, {forceDisabled}, {now})
            """, ct);
        return new(FeatureControlSaveStatus.Saved, new(target, enabled, forceDisabled ?? false, version, now));
    }

    private async Task<FeatureControlState> ReadCoreAsync(string environment, FeatureControlTarget target, CancellationToken ct)
    {
        if (target.OrganizationId is Guid organizationId)
        {
            OrganizationFeatureOverride? row = await context.Set<OrganizationFeatureOverride>().AsNoTracking()
                .SingleOrDefaultAsync(row => row.Environment == environment && row.FeatureKey == target.FeatureKey
                    && row.OrganizationId == organizationId, ct);
            return row is null ? Missing(target) : new(target, row.Enabled, false, row.Version, row.UpdatedAtUtc);
        }
        FeatureEnvironmentControl? control = await context.Set<FeatureEnvironmentControl>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.Environment == environment && row.FeatureKey == target.FeatureKey, ct);
        return control is null ? Missing(target) : new(target, control.DefaultEnabled, control.ForceDisabled, control.Version, control.UpdatedAtUtc);
    }

    private async Task<DbTransaction> RequireScopeAsync(Guid actorId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        DbTransaction? transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        if (actorId == Guid.Empty || transaction?.Connection is null || transaction.IsolationLevel != IsolationLevel.ReadCommitted)
            throw new InvalidOperationException("Feature controls require an active ReadCommitted platform transaction and actor.");
        AmbientScope scope = await context.Database.SqlQuery<AmbientScope>($"""
            SELECT current_user::text AS "Role", NULLIF(current_setting('app.actor_id', true), '')::uuid AS "ActorId"
            """).SingleAsync(ct);
        if (scope.Role != "trykatch_platform_runtime" || scope.ActorId != actorId)
            throw new InvalidOperationException("Feature control persistence requires its matching platform actor and profile.");
        return transaction;
    }

    private static void ValidateTarget(string environment, FeatureControlTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (environment is null || !EnvironmentPattern.IsMatch(environment))
            throw new ArgumentException("Feature environment identity is invalid.", nameof(environment));
        if (string.IsNullOrWhiteSpace(target.FeatureKey) || target.FeatureKey.Length > 120
            || !FeaturePattern.IsMatch(target.FeatureKey) || !target.FeatureKey.Contains('.', StringComparison.Ordinal)
            || target.OrganizationId == Guid.Empty)
            throw new ArgumentException("Feature control target is invalid.", nameof(target));
    }

    private static FeatureControlState Missing(FeatureControlTarget target) => new(target, null, false, Guid.Empty, null);
    private sealed record AcquiredTarget(DbTransaction Transaction, Guid ActorId, string Environment, FeatureControlTarget Target);
    private sealed class AmbientScope
    {
        public string Role { get; set; } = string.Empty;
        public Guid? ActorId { get; set; }
    }
}
