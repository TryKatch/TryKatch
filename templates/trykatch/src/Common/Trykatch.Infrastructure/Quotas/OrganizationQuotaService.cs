using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Trykatch.Application.Organizations;
using Trykatch.Application.Quotas;
using Trykatch.Domain.Organizations;
using Trykatch.Infrastructure.Execution;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Quotas;

/// <summary>Caller-owned quota holds, entitlements and confirmed removals. Uploads use their closed composite lifecycle.</summary>
internal sealed partial class OrganizationQuotaService(
    ApplicationDbContext application,
    OrganizationControlPlaneDbContext control,
    IOrganizationContext organization,
    IOrganizationAccessResolver accessResolver,
    IOrganizationDataPlacement placement,
    ModuleCatalog catalog,
    ModuleWorkExecutionContext work,
    TimeProvider timeProvider,
    QuotaPublicationAuthority publication,
    IOrganizationEntitlementProjectionPublisher publisher,
    QuotaWorkflowRegistry workflows) : IOrganizationQuota, IOrganizationEntitlementReader, IOrganizationUsageLedger
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReservationLifetime = TimeSpan.FromMinutes(15);
    private readonly OrganizationEntitlementStore entitlements = new(application, catalog);
    private readonly OrganizationUsageLedger ledger = new(application);

    public async Task<OrganizationEntitlement> GetAsync(string resourceKey, CancellationToken cancellationToken = default)
    {
        RequireTransaction();
        cancellationToken.ThrowIfCancellationRequested();
        if (!entitlements.TryGetResource(resourceKey, out string owner, out ModuleQuotaResourceDescriptor? resource))
            throw new OrganizationQuotaUnavailableException();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OperationTimeout);
        try
        {
            CancellationToken token = timeout.Token;
            if (!await HasAuthorityAsync(owner, resourceKey, token)) throw new OrganizationQuotaUnavailableException();
            QuotaPublicationReceipt receipt = await publication.EnsurePublishedAsync(publisher, token);
            if (!await QuotaResourceLock.TryAcquireAsync(application, organization.OrganizationId, resourceKey, token))
                throw new OrganizationQuotaUnavailableException();
            publication.ResourceLockAcquired();
            if (!await HasAuthorityAsync(owner, resourceKey, token)) throw new OrganizationQuotaUnavailableException();
            OrganizationEntitlementRecord? projection = await entitlements.ReadAsync(organization.OrganizationId, resourceKey, token);
            if (projection is null || projection.OwnerModuleId != owner || projection.Unit != resource!.Unit
                || !receipt.IsSatisfiedBy(projection))
                throw new OrganizationQuotaUnavailableException();
            return OrganizationEntitlementStore.ToContract(projection);
        }
        catch (OperationCanceledException failure) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OrganizationQuotaUnavailableException(failure);
        }
        catch (Exception failure) when (IsDatabaseFailure(failure))
        {
            throw new OrganizationQuotaUnavailableException(failure);
        }
    }

    public async Task<QuotaReservationResult> ReserveAsync(string resourceKey, long units, Guid operationId,
        CancellationToken cancellationToken = default)
    {
        RequireTransaction();
        cancellationToken.ThrowIfCancellationRequested();
        if (operationId == Guid.Empty || units <= 0)
            return Denied("quota_request_invalid");
        if (!entitlements.TryGetResource(resourceKey, out string owner, out ModuleQuotaResourceDescriptor? resource))
            return Denied("quota_resource_unavailable");
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OperationTimeout);
        try
        {
            CancellationToken token = timeout.Token;
            if (!await HasAuthorityAsync(owner, resourceKey, token)) return Denied("quota_authority_denied");
            QuotaPublicationReceipt receipt = await publication.EnsurePublishedAsync(publisher, token);
            if (!await QuotaResourceLock.TryAcquireAsync(application, organization.OrganizationId, resourceKey, token))
                return Unavailable("quota_busy");
            publication.ResourceLockAcquired();
            // Re-resolve after acquisition: neither a retry nor a busy identity confers authority.
            if (!await HasAuthorityAsync(owner, resourceKey, token)) return Denied("quota_authority_denied");
            OrganizationEntitlementRecord? projection = await entitlements.ReadAsync(organization.OrganizationId, resourceKey, token);
            if (projection is null) return Denied("quota_unconfigured");
            if (projection.OwnerModuleId != owner || projection.Unit != resource!.Unit)
                return Unavailable("quota_projection_invalid");

            OrganizationQuotaReservationRecord? existing = await application.Set<OrganizationQuotaReservationRecord>().AsNoTracking()
                .SingleOrDefaultAsync(row => row.OrganizationId == organization.OrganizationId
                    && row.ResourceKey == resourceKey && row.OperationId == operationId, token);
            if (existing is not null)
                return existing.ActorId == organization.ActorId && existing.Units == units
                    && existing.EffectKind == QuotaReservationEffectKind.TransactionOnly
                    && existing.State == ModuleQuotaReservationState.Reserved
                        ? new(QuotaReservationDisposition.AlreadyReserved, ToContract(existing))
                        : new(QuotaReservationDisposition.Conflict, FailureCode: "quota_conflict");

            if (!receipt.IsSatisfiedBy(projection)) return Unavailable("quota_projection_stale");

            (long committed, long held) = await ledger.TotalsAsync(organization.OrganizationId, resourceKey, token);
            ModuleQuotaInventorySourceRef? source = resource.InventorySource;
            bool inventoryComplete = await application.Set<OrganizationQuotaInventoryRecord>().AsNoTracking()
                .AnyAsync(row => row.OrganizationId == organization.OrganizationId && row.ResourceKey == resourceKey
                    && row.State == QuotaInventoryState.Complete
                    && (source == null || row.SourceKey == source.SourceKey && row.SourceVersion == source.SourceVersion), token);
            QuotaCapacityDecision decision = QuotaCapacityPolicy.Evaluate(
                OrganizationEntitlementStore.ToContract(projection), inventoryComplete, committed, held, units);
            if (decision.Disposition != QuotaReservationDisposition.Reserved)
                return new(decision.Disposition, FailureCode: decision.FailureCode);

            DateTimeOffset now = UtcNow();
            OrganizationQuotaReservationRecord reservation = new()
            {
                OrganizationId = organization.OrganizationId, Id = Guid.CreateVersion7(), ResourceKey = resourceKey,
                ActorId = organization.ActorId, Units = units, OperationId = operationId, EntitlementVersion = projection.Version,
                EffectKind = QuotaReservationEffectKind.TransactionOnly, State = ModuleQuotaReservationState.Reserved,
                CreatedAtUtc = now, ExpiresAtUtc = now + ReservationLifetime
            };
            await application.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO app.organization_quota_reservations
                    ("OrganizationId", "Id", "ResourceKey", "ActorId", "Units", "OperationId", "EntitlementVersion",
                     "EffectKind", "State", "CreatedAtUtc", "ExpiresAtUtc", "ClosedAtUtc")
                VALUES ({reservation.OrganizationId}, {reservation.Id}, {resourceKey}, {reservation.ActorId}, {units},
                        {operationId}, {projection.Version}, 1, 0, {now}, {reservation.ExpiresAtUtc}, NULL)
                """, token);
            return new(QuotaReservationDisposition.Reserved, ToContract(reservation));
        }
        catch (OrganizationQuotaUnavailableException)
        {
            return Unavailable("quota_unavailable");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable("quota_unavailable");
        }
        catch (PostgresException failure) when (failure.SqlState == PostgresErrorCodes.NumericValueOutOfRange)
        {
            return Unavailable("quota_usage_invalid");
        }
        catch (Exception failure) when (IsDatabaseFailure(failure))
        {
            return Unavailable("quota_unavailable");
        }
    }

    public Task CommitAsync(Guid reservationId, CancellationToken cancellationToken = default) =>
        TransitionAsync(reservationId, ModuleQuotaReservationState.Committed, cancellationToken);

    public Task ReleaseAsync(Guid reservationId, CancellationToken cancellationToken = default) =>
        TransitionAsync(reservationId, ModuleQuotaReservationState.Released, cancellationToken);

    private async Task TransitionAsync(Guid reservationId, ModuleQuotaReservationState target, CancellationToken cancellationToken)
    {
        RequireTransaction();
        cancellationToken.ThrowIfCancellationRequested();
        if (!organization.IsResolved || reservationId == Guid.Empty) throw new OrganizationQuotaConflictException();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OperationTimeout);
        try
        {
            CancellationToken token = timeout.Token;
            OrganizationQuotaReservationRecord? reservation = await application.Set<OrganizationQuotaReservationRecord>().AsNoTracking()
                .SingleOrDefaultAsync(row => row.OrganizationId == organization.OrganizationId && row.Id == reservationId, token);
            if (reservation is null || !entitlements.TryGetResource(reservation.ResourceKey, out string owner,
                    out ModuleQuotaResourceDescriptor? resource)) throw new OrganizationQuotaConflictException();
            if (!await HasAuthorityAsync(owner, reservation.ResourceKey, token)) throw new OrganizationQuotaUnavailableException();
            await publication.EnsurePublishedAsync(publisher, token);
            if (!await QuotaResourceLock.TryAcquireAsync(application, organization.OrganizationId, reservation.ResourceKey, token))
                throw new OrganizationQuotaUnavailableException();
            publication.ResourceLockAcquired();
            if (!await HasAuthorityAsync(owner, reservation.ResourceKey, token)) throw new OrganizationQuotaUnavailableException();
            reservation = await application.Set<OrganizationQuotaReservationRecord>().AsNoTracking()
                .SingleAsync(row => row.OrganizationId == organization.OrganizationId && row.Id == reservationId, token);
            if (reservation.ActorId != organization.ActorId || reservation.EffectKind != QuotaReservationEffectKind.TransactionOnly)
                throw new OrganizationQuotaConflictException();
            OrganizationEntitlementRecord? projection = await entitlements.ReadAsync(organization.OrganizationId, reservation.ResourceKey, token);
            if (projection is null || projection.OwnerModuleId != owner || projection.Unit != resource!.Unit)
                throw new OrganizationQuotaUnavailableException();
            if (reservation.State == target) return;
            if (reservation.State != ModuleQuotaReservationState.Reserved) throw new OrganizationQuotaConflictException();

            DateTimeOffset now = UtcNow();
            // One statement closes and charges the hold, including cancellation/error rollback.
            int changed = target == ModuleQuotaReservationState.Committed
                ? await ledger.CommitReservationAsync(reservation, now, token)
                : await application.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE app.organization_quota_reservations SET "State" = {(int)target}, "ClosedAtUtc" = {now}
                WHERE "OrganizationId" = {organization.OrganizationId} AND "Id" = {reservationId} AND "State" = 0
                """, token);
            if (changed != 1) throw new OrganizationQuotaConflictException();
        }
        catch (OperationCanceledException failure) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OrganizationQuotaUnavailableException(failure);
        }
        catch (Exception failure) when (IsDatabaseFailure(failure))
        {
            throw new OrganizationQuotaUnavailableException(failure);
        }
    }

    private async Task<bool> HasAuthorityAsync(string owner, string resourceKey, CancellationToken token)
    {
        if (!organization.IsResolved || organization.OrganizationId == Guid.Empty || organization.ActorId == Guid.Empty) return false;
        bool matchesScope = await application.Database.SqlQuery<bool>($"""
            SELECT (NULLIF(current_setting('app.organization_id', true), '')::uuid = {organization.OrganizationId}
                AND NULLIF(current_setting('app.actor_id', true), '')::uuid = {organization.ActorId}) IS TRUE AS "Value"
            """).SingleAsync(token);
        if (!matchesScope) throw new InvalidOperationException("Quota authority must match the enlisted organization and actor scope.");
        OrganizationDataRoute route;
        try { route = await placement.ResolveAsync(organization.OrganizationId, token); }
        catch (Exception failure) when (failure is KeyNotFoundException or InvalidOperationException { InnerException: null })
        {
            return false;
        }
        if (route.OrganizationId != organization.OrganizationId || route.Placement != OrganizationDataPlacementKind.Shared
            || route.Provider != "postgres" || route.DatabaseIdentifier != application.Database.GetDbConnection().Database) return false;

        try
        {
            Guid membership = organization.MembershipId;
            OrganizationAccess? fresh = await accessResolver.ResolveAsync(organization.ActorId, organization.OrganizationId, token);
            return membership != Guid.Empty && fresh is not null && fresh.MembershipId == membership
                && fresh.OrganizationId == organization.OrganizationId && fresh.ActorId == organization.ActorId;
        }
        catch (OrganizationMembershipRequiredException)
        {
            if (!QuotaSystemAuthority.IsAllowed(work, catalog, organization.OrganizationId,
                    organization.ActorId, owner, resourceKey)) return false;
            return await control.Organizations.AsNoTracking()
                .AnyAsync(row => row.Id == organization.OrganizationId && row.IsActive, token);
        }
    }

    private void RequireTransaction()
    {
        DbTransaction? transaction = application.Database.CurrentTransaction?.GetDbTransaction();
        if (transaction is null || transaction.Connection is null || transaction.IsolationLevel != IsolationLevel.ReadCommitted
            || !ReferenceEquals(transaction, control.Database.CurrentTransaction?.GetDbTransaction()))
            throw new InvalidOperationException("Quotas require the same active ReadCommitted organization transaction.");
    }

    private DateTimeOffset UtcNow() => new(timeProvider.GetUtcNow().UtcTicks / 10 * 10, TimeSpan.Zero);
    private static bool IsDatabaseFailure(Exception failure) => failure is DbException or OverflowException or TimeoutException
        or InvalidOperationException { InnerException: DbException };
    private static QuotaReservationResult Denied(string code) => new(QuotaReservationDisposition.Denied, FailureCode: code);
    private static QuotaReservationResult Unavailable(string code) => new(QuotaReservationDisposition.Unavailable, FailureCode: code);
    private static ModuleQuotaReservation ToContract(OrganizationQuotaReservationRecord record) => new(
        record.Id, record.ResourceKey, record.Units, record.OperationId, record.EntitlementVersion, record.ExpiresAtUtc, record.State);
}
