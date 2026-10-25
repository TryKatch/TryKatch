using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Trykatch.Application.Organizations;
using Trykatch.Application.Quotas;
using Trykatch.Domain.Organizations;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Infrastructure.Storage;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Quotas;

internal sealed class ModuleUploadAdmission(
    IServiceScopeFactory scopes,
    ApplicationDbContext application,
    OrganizationControlPlaneDbContext control,
    IOrganizationContext organization,
    IOrganizationAccessResolver accessResolver,
    IOrganizationDataPlacement placement,
    ModuleCatalog catalog,
    QuotaWorkflowRegistry workflows,
    QuotaPublicationAuthority publication,
    IOrganizationEntitlementProjectionPublisher publisher,
    IConditionalObjectStorage storage,
    TimeProvider clock,
    UploadFinalizationCommitGuard commitGuard,
    ModuleUploadFencing fencing) : IModuleUploadAdmission
{
    private static readonly TimeSpan AdmissionTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    public async Task<ModuleUploadReserveResult> ReserveAsync(ModuleUploadRequest request, CancellationToken cancellationToken = default)
    {
        ParentTransaction parent = RequireTransaction();
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null || !ConditionalObjectPolicy.TryIdentity(
                new(request.OperationId, request.Length, request.Sha256, request.MetadataFingerprint), out ObjectWriteIdentity identity)
            || !ConditionalObjectPolicy.IsHash(storage.NamespaceFingerprint))
            return new(ModuleUploadReserveDisposition.Denied, FailureCode: "upload_request_invalid");
        if (!workflows.TryGetUpload(request.Policy, out InstalledModuleQuotaResource? installed)
            || !TryGetUser(out UserIdentity? user))
            return new(ModuleUploadReserveDisposition.Denied, FailureCode: "upload_authority_denied");

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AdmissionTimeout);
        try
        {
            CancellationToken token = timeout.Token;
            publication.EnsureChildAdmissionAllowed();
            if (!await HasAuthorityAsync(user!, installed!, token))
                return new(ModuleUploadReserveDisposition.Denied, FailureCode: "upload_authority_denied");

            await using AsyncServiceScope child = scopes.CreateAsyncScope();
            IServiceProvider services = child.ServiceProvider;
            await using IOrganizationDataScope transaction = await services.GetRequiredService<IOrganizationDataScopeFactory>()
                .BeginAsync(user!.OrganizationId, user.ActorId, token);
            OrganizationControlPlaneDbContext childControl = services.GetRequiredService<OrganizationControlPlaneDbContext>();
            ApplicationDbContext childApplication = services.GetRequiredService<ApplicationDbContext>();
            _ = await OrganizationTransactionEnlistment.EnlistAsync(childControl, childApplication,
                user.OrganizationId, user.ActorId, token);
            await childApplication.Database.ExecuteSqlRawAsync("SET LOCAL statement_timeout = '5s'; SET LOCAL lock_timeout = '1s'", token);
            OrganizationAccess? fresh = await services.GetRequiredService<IOrganizationAccessResolver>()
                .ResolveAsync(user.ActorId, user.OrganizationId, token);
            if (fresh is null || fresh.MembershipId != user.MembershipId)
                return new(ModuleUploadReserveDisposition.Denied, FailureCode: "upload_authority_denied");
            services.GetRequiredService<IOrganizationContextInitializer>().Initialize(fresh);

            AdmissionOutcome outcome = await services.GetRequiredService<ModuleUploadAdmission>()
                .ReserveInCurrentTransactionAsync(request.Policy, identity, user, token);
            await transaction.CommitAsync(token);
            // A lost parent after durable admission leaves recovery work, never permission to PUT.
            if (RequireTransaction() != parent)
                throw new InvalidOperationException("Upload admission requires its original parent transaction.");
            return new(outcome.Disposition, outcome.Operation is { } operation
                ? new Receipt(this, parent, request.Policy, operation) : null, outcome.FailureCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(ModuleUploadReserveDisposition.Unavailable, FailureCode: "upload_unavailable");
        }
        catch (Exception failure) when (IsUnavailable(failure))
        {
            return new(ModuleUploadReserveDisposition.Unavailable, FailureCode: "upload_unavailable");
        }
    }

    private async Task<AdmissionOutcome> ReserveInCurrentTransactionAsync(ModuleUploadPolicyRef policy,
        ObjectWriteIdentity identity, UserIdentity user, CancellationToken token)
    {
        RequireTransaction();
        if (!workflows.TryGetUpload(policy, out InstalledModuleQuotaResource? installed)
            || !await HasAuthorityAsync(user, installed!, token))
            return new(ModuleUploadReserveDisposition.Denied, FailureCode: "upload_authority_denied");
        ModuleQuotaResourceDescriptor resource = installed!.Resource;
        QuotaPublicationReceipt published = await publication.EnsurePublishedAsync(publisher, token);
        if (!await QuotaResourceLock.TryAcquireAsync(application, user.OrganizationId, resource.Key, token))
            return new(ModuleUploadReserveDisposition.Unavailable, FailureCode: "quota_busy");
        publication.ResourceLockAcquired();
        if (!await HasAuthorityAsync(user, installed, token))
            return new(ModuleUploadReserveDisposition.Denied, FailureCode: "upload_authority_denied");
        OrganizationEntitlementRecord? entitlement = await new OrganizationEntitlementStore(application, catalog)
            .ReadAsync(user.OrganizationId, resource.Key, token);
        if (entitlement is null || entitlement.OwnerModuleId != installed.ModuleId || entitlement.Unit != resource.Unit)
            return new(ModuleUploadReserveDisposition.Unavailable, FailureCode: "quota_projection_invalid");

        OrganizationQuotaReservationRecord? hold = await application.Set<OrganizationQuotaReservationRecord>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.OrganizationId == user.OrganizationId && row.ResourceKey == resource.Key
                && row.OperationId == identity.OperationId, token);
        ModuleUploadOperationRecord? existing = await application.Set<ModuleUploadOperationRecord>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.OrganizationId == user.OrganizationId && row.ResourceKey == resource.Key
                && row.OperationId == identity.OperationId, token);
        if (existing is not null || hold is not null)
        {
            if (existing is null || hold is null || !Matches(existing, policy, identity, user)
                || !MatchesHold(existing, hold))
                return new(ModuleUploadReserveDisposition.Conflict, FailureCode: "upload_conflict");
            if (existing.State == ModuleUploadState.Committed)
                return new(ModuleUploadReserveDisposition.AlreadyCommitted, existing);
            if (existing.State != ModuleUploadState.Admitted)
                return new(ModuleUploadReserveDisposition.Conflict, FailureCode: "upload_conflict");
            return existing.ExpiresAtUtc > Now()
                ? new(ModuleUploadReserveDisposition.AlreadyReserved, existing)
                : new(ModuleUploadReserveDisposition.Unavailable, FailureCode: "upload_expired");
        }
        if (!published.IsSatisfiedBy(entitlement))
            return new(ModuleUploadReserveDisposition.Unavailable, FailureCode: "quota_projection_stale");

        ModuleQuotaInventorySourceRef? source = resource.InventorySource;
        bool complete = await application.Set<OrganizationQuotaInventoryRecord>().AsNoTracking()
            .AnyAsync(row => row.OrganizationId == user.OrganizationId && row.ResourceKey == resource.Key
                && row.State == QuotaInventoryState.Complete
                && (source == null || row.SourceKey == source.SourceKey && row.SourceVersion == source.SourceVersion), token);
        DateTimeOffset now = Now();
        if (!complete && source is not null)
        {
            // INSERT-only organization role: no queue reads or coordinator privilege are needed.
            await application.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO infrastructure.quota_maintenance_work
                    ("OrganizationId", "Id", "Kind", "ResourceKey", "ReservationId", "UploadOperationId", "State",
                     "DueAtUtc", "CreatedAtUtc", "Attempts", "LeaseId", "LeaseUntilUtc", "LastFailureCode")
                VALUES ({user.OrganizationId}, {Guid.CreateVersion7()}, 3, {resource.Key}, NULL, NULL, 0,
                    {now}, {now}, 0, NULL, NULL, NULL)
                ON CONFLICT DO NOTHING
                """, token);
        }
        (long committed, long held) = await new OrganizationUsageLedger(application).TotalsAsync(user.OrganizationId, resource.Key, token);
        QuotaCapacityDecision capacity = QuotaCapacityPolicy.Evaluate(OrganizationEntitlementStore.ToContract(entitlement),
            complete, committed, held, identity.OriginalLength);
        if (capacity.Disposition != QuotaReservationDisposition.Reserved)
            return new(capacity.Disposition == QuotaReservationDisposition.Denied
                ? ModuleUploadReserveDisposition.Denied : ModuleUploadReserveDisposition.Unavailable,
                FailureCode: capacity.FailureCode);

        Guid reservationId = Guid.CreateVersion7();
        Guid subjectId = Guid.CreateVersion7();
        ModuleUploadOperationRecord operation = new()
        {
            OrganizationId = user.OrganizationId, ResourceKey = resource.Key, OperationId = identity.OperationId,
            ActorId = user.ActorId, MembershipId = user.MembershipId, ReservationId = reservationId, SubjectId = subjectId,
            ObjectKey = ObjectKey(user.OrganizationId, installed.ModuleId, identity.OperationId, subjectId),
            StorageNamespaceFingerprint = storage.NamespaceFingerprint, Length = identity.OriginalLength,
            Sha256 = identity.Sha256, MetadataFingerprint = identity.MetadataFingerprint,
            UploadPolicyKey = policy.PolicyKey, UploadPolicyVersion = policy.PolicyVersion,
            State = ModuleUploadState.Admitted, CreatedAtUtc = now, ExpiresAtUtc = now + Lifetime
        };
        await application.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO app.organization_quota_reservations
                ("OrganizationId", "Id", "ResourceKey", "ActorId", "Units", "OperationId", "EntitlementVersion",
                 "EffectKind", "State", "CreatedAtUtc", "ExpiresAtUtc", "ClosedAtUtc")
            VALUES ({user.OrganizationId}, {reservationId}, {resource.Key}, {user.ActorId}, {operation.Length},
                {operation.OperationId}, {entitlement.Version}, 2, 0, {now}, {operation.ExpiresAtUtc}, NULL)
            """, token);
        await application.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO app.module_upload_operations
                ("OrganizationId", "ResourceKey", "OperationId", "ActorId", "MembershipId", "ReservationId", "SubjectId",
                 "ObjectKey", "StorageNamespaceFingerprint", "Length", "Sha256", "MetadataFingerprint", "UploadPolicyKey",
                 "UploadPolicyVersion", "State", "CreatedAtUtc", "ExpiresAtUtc", "ClosedAtUtc")
            VALUES ({operation.OrganizationId}, {resource.Key}, {operation.OperationId}, {operation.ActorId},
                {operation.MembershipId}, {reservationId}, {subjectId}, {operation.ObjectKey}, {operation.StorageNamespaceFingerprint},
                {operation.Length}, {operation.Sha256}, {operation.MetadataFingerprint}, {policy.PolicyKey}, {policy.PolicyVersion},
                0, {now}, {operation.ExpiresAtUtc}, NULL)
            """, token);
        await application.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO infrastructure.quota_maintenance_work
                ("OrganizationId", "Id", "Kind", "ResourceKey", "ReservationId", "UploadOperationId", "State",
                 "DueAtUtc", "CreatedAtUtc", "Attempts", "LeaseId", "LeaseUntilUtc", "LastFailureCode")
            VALUES ({user.OrganizationId}, {Guid.CreateVersion7()}, 2, {resource.Key}, {reservationId}, {operation.OperationId},
                0, {operation.ExpiresAtUtc}, {now}, 0, NULL, NULL, NULL)
            """, token);
        return new(ModuleUploadReserveDisposition.Reserved, operation);
    }

    public async Task<ModuleUploadFinalization<T>> FinalizeAsync<T>(ModuleUploadReceipt receipt,
        Func<CancellationToken, Task<T>> finalize, CancellationToken cancellationToken = default)
    {
        ParentTransaction parent = RequireTransaction();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(finalize);
        if (receipt is not Receipt issued || !ReferenceEquals(issued.Issuer, this) || issued.Parent != parent)
            return new(ModuleUploadFinalizationDisposition.Conflict, FailureCode: "upload_conflict");
        if (!TryGetUser(out UserIdentity? user) || !workflows.TryGetUpload(issued.Policy, out InstalledModuleQuotaResource? installed))
            return new(ModuleUploadFinalizationDisposition.Unavailable, FailureCode: "upload_authority_denied");
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        try
        {
            CancellationToken token = timeout.Token;
            if (!await HasAuthorityAsync(user!, installed!, token))
                return new(ModuleUploadFinalizationDisposition.Unavailable, FailureCode: "upload_authority_denied");
            await publication.EnsurePublishedAsync(publisher, token);
            if (!await QuotaResourceLock.TryAcquireAsync(application, user!.OrganizationId, installed!.Resource.Key, token))
                return new(ModuleUploadFinalizationDisposition.Unavailable, FailureCode: "quota_busy");
            publication.ResourceLockAcquired();
            if (!await HasAuthorityAsync(user, installed, token))
                return new(ModuleUploadFinalizationDisposition.Unavailable, FailureCode: "upload_authority_denied");
            ModuleUploadOperationRecord? operation = await application.Set<ModuleUploadOperationRecord>().AsNoTracking()
                .SingleOrDefaultAsync(row => row.OrganizationId == user.OrganizationId && row.ResourceKey == installed.Resource.Key
                    && row.OperationId == issued.OperationId, token);
            OrganizationQuotaReservationRecord? hold = await application.Set<OrganizationQuotaReservationRecord>().AsNoTracking()
                .SingleOrDefaultAsync(row => row.OrganizationId == user.OrganizationId && row.Id == issued.Operation.ReservationId, token);
            if (operation is null || hold is null || !SameAdmission(operation, issued.Operation)
                || !Matches(operation, issued.Policy, issued.Identity, user) || !MatchesHold(operation, hold))
                return new(ModuleUploadFinalizationDisposition.Conflict, FailureCode: "upload_conflict");
            if (operation.State == ModuleUploadState.Committed)
                return new(ModuleUploadFinalizationDisposition.AlreadyCommitted, SubjectId: operation.SubjectId);
            if (operation.State != ModuleUploadState.Admitted)
                return new(ModuleUploadFinalizationDisposition.Conflict, FailureCode: "upload_conflict");
            if (operation.ExpiresAtUtc <= Now())
                return new(ModuleUploadFinalizationDisposition.Unavailable, FailureCode: "upload_expired");
            ConditionalObjectStat stored = await storage.StatAsync(operation.ObjectKey, token);
            if (stored.State != ConditionalObjectState.Provisional || stored.Revision is null)
                return new(ModuleUploadFinalizationDisposition.Unavailable, FailureCode: "upload_object_unverified");
            if (stored.Length != operation.Length || !ConditionalObjectPolicy.SameIdentity(stored.Identity, issued.Identity))
                return new(ModuleUploadFinalizationDisposition.Conflict, FailureCode: "upload_object_conflict");

            // From here, any failure must abort the whole parent, even if its caller catches it.
            UploadFinalizationCommitGuard.Attempt attempt = commitGuard.Begin(parent.Transaction);
            T value = await finalize(token);
            if (RequireTransaction() != parent || operation.ExpiresAtUtc <= Now()
                || !await HasAuthorityAsync(user, installed, token))
                throw new UploadFinalizationIncompleteException();
            DateTimeOffset now = Now();
            await new OrganizationUsageLedger(application).AppendUploadAsync(operation, now, token);
            int closedHold = await application.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE app.organization_quota_reservations SET "State" = 1, "ClosedAtUtc" = {now}
                WHERE "OrganizationId" = {operation.OrganizationId} AND "Id" = {operation.ReservationId}
                    AND "EffectKind" = 2 AND "State" = 0
                """, token);
            int closedUpload = await application.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE app.module_upload_operations SET "State" = 1, "ClosedAtUtc" = {now}
                WHERE "OrganizationId" = {operation.OrganizationId} AND "ResourceKey" = {operation.ResourceKey}
                    AND "OperationId" = {operation.OperationId} AND "State" = 0
                """, token);
            if (closedHold != 1 || closedUpload != 1) throw new UploadFinalizationIncompleteException();
            commitGuard.Complete(attempt);
            return new(ModuleUploadFinalizationDisposition.Committed, value, operation.SubjectId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(ModuleUploadFinalizationDisposition.Unavailable, FailureCode: "upload_unavailable");
        }
        catch (Exception failure) when (IsUnavailable(failure))
        {
            return new(ModuleUploadFinalizationDisposition.Unavailable, FailureCode: "upload_unavailable");
        }
    }

    public Task<ModuleUploadCancelResult> CancelAsync(ModuleUploadReceipt receipt, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return receipt is Receipt issued && ReferenceEquals(issued.Issuer, this)
            ? fencing.CancelAdmittedAsync(issued.Operation, cancellationToken)
            : Task.FromResult(new ModuleUploadCancelResult(ModuleUploadCancelDisposition.Conflict, "upload_conflict"));
    }

    private bool TryGetUser(out UserIdentity? user)
    {
        user = null;
        if (!organization.IsResolved || organization.OrganizationId == Guid.Empty || organization.ActorId == Guid.Empty) return false;
        try
        {
            Guid membership = organization.MembershipId;
            if (membership == Guid.Empty) return false;
            user = new(organization.OrganizationId, organization.ActorId, membership);
            return true;
        }
        catch (OrganizationMembershipRequiredException) { return false; }
    }

    private async Task<bool> HasAuthorityAsync(UserIdentity user, InstalledModuleQuotaResource installed, CancellationToken token)
    {
        if (!TryGetUser(out UserIdentity? current) || current != user) return false;
        bool matches = await application.Database.SqlQuery<bool>($"""
            SELECT (NULLIF(current_setting('app.organization_id', true), '')::uuid = {user.OrganizationId}
                AND NULLIF(current_setting('app.actor_id', true), '')::uuid = {user.ActorId}) IS TRUE AS "Value"
            """).SingleAsync(token);
        if (!matches) throw new InvalidOperationException("Upload authority must match the current organization and actor frame.");
        OrganizationDataRoute route;
        try { route = await placement.ResolveAsync(user.OrganizationId, token); }
        catch (Exception failure) when (failure is KeyNotFoundException or InvalidOperationException { InnerException: null })
        {
            return false;
        }
        if (route.OrganizationId != user.OrganizationId || route.Placement != OrganizationDataPlacementKind.Shared
            || route.Provider != "postgres" || route.DatabaseIdentifier != application.Database.GetDbConnection().Database) return false;
        OrganizationAccess? fresh = await accessResolver.ResolveAsync(user.ActorId, user.OrganizationId, token);
        string required = installed.Resource.UploadPolicy!.RequiredPermission;
        return fresh is not null && fresh.OrganizationId == user.OrganizationId && fresh.ActorId == user.ActorId
            && fresh.MembershipId == user.MembershipId && fresh.Permissions.Contains(required)
            && organization.Permissions.Contains(required);
    }

    private ParentTransaction RequireTransaction()
    {
        IDbContextTransaction? owner = control.Database.CurrentTransaction;
        DbTransaction? transaction = application.Database.CurrentTransaction?.GetDbTransaction();
        if (owner is null || transaction is null || transaction.Connection is null
            || transaction.IsolationLevel != IsolationLevel.ReadCommitted || !ReferenceEquals(transaction, owner.GetDbTransaction()))
            throw new InvalidOperationException("Uploads require the same active ReadCommitted organization transaction.");
        return new(transaction, owner.TransactionId);
    }

    private bool Matches(ModuleUploadOperationRecord operation, ModuleUploadPolicyRef policy, ObjectWriteIdentity identity, UserIdentity user) =>
        operation.OrganizationId == user.OrganizationId && operation.ActorId == user.ActorId && operation.MembershipId == user.MembershipId
        && operation.UploadPolicyKey == policy.PolicyKey && operation.UploadPolicyVersion == policy.PolicyVersion
        && operation.StorageNamespaceFingerprint == storage.NamespaceFingerprint
        && ConditionalObjectPolicy.SameIdentity(Identity(operation), identity)
        && operation.ObjectKey == ObjectKey(user.OrganizationId, policy.OwnerModuleId, operation.OperationId, operation.SubjectId);

    internal static bool MatchesHold(ModuleUploadOperationRecord operation, OrganizationQuotaReservationRecord hold) =>
        hold.OrganizationId == operation.OrganizationId && hold.ResourceKey == operation.ResourceKey && hold.Id == operation.ReservationId
        && hold.OperationId == operation.OperationId && hold.ActorId == operation.ActorId && hold.Units == operation.Length
        && hold.EffectKind == QuotaReservationEffectKind.Upload && hold.CreatedAtUtc == operation.CreatedAtUtc
        && hold.ExpiresAtUtc == operation.ExpiresAtUtc
        && hold.State == (operation.State switch
        {
            ModuleUploadState.Committed => ModuleQuotaReservationState.Committed,
            ModuleUploadState.Cancelled => ModuleQuotaReservationState.Released,
            _ => ModuleQuotaReservationState.Reserved
        });

    internal static bool SameAdmission(ModuleUploadOperationRecord left, ModuleUploadOperationRecord right) =>
        left.OrganizationId == right.OrganizationId && left.ResourceKey == right.ResourceKey && left.OperationId == right.OperationId
        && left.ActorId == right.ActorId && left.MembershipId == right.MembershipId && left.ReservationId == right.ReservationId
        && left.SubjectId == right.SubjectId && left.ObjectKey == right.ObjectKey
        && left.StorageNamespaceFingerprint == right.StorageNamespaceFingerprint && left.Length == right.Length
        && left.Sha256 == right.Sha256 && left.MetadataFingerprint == right.MetadataFingerprint
        && left.UploadPolicyKey == right.UploadPolicyKey && left.UploadPolicyVersion == right.UploadPolicyVersion
        && left.CreatedAtUtc == right.CreatedAtUtc && left.ExpiresAtUtc == right.ExpiresAtUtc;

    internal static ObjectWriteIdentity Identity(ModuleUploadOperationRecord operation) =>
        new(operation.OperationId, operation.Length, operation.Sha256, operation.MetadataFingerprint);
    internal static string ObjectKey(Guid organizationId, string moduleId, Guid operationId, Guid subjectId) =>
        $"{ConditionalObjectPolicy.ManagedPrefix}{organizationId:N}/{moduleId}/{operationId:N}/{subjectId:N}";
    private DateTimeOffset Now() => new(clock.GetUtcNow().UtcTicks / 10 * 10, TimeSpan.Zero);
    private static bool IsUnavailable(Exception failure) => failure is OrganizationQuotaUnavailableException or DbException
        or TimeoutException or OverflowException or InvalidOperationException { InnerException: DbException };

    private sealed record UserIdentity(Guid OrganizationId, Guid ActorId, Guid MembershipId);
    private sealed record ParentTransaction(DbTransaction Transaction, Guid LifetimeId);
    private sealed record AdmissionOutcome(ModuleUploadReserveDisposition Disposition, ModuleUploadOperationRecord? Operation = null,
        string? FailureCode = null);

    private sealed class Receipt(ModuleUploadAdmission issuer, ParentTransaction parent, ModuleUploadPolicyRef policy,
        ModuleUploadOperationRecord operation) : ModuleUploadReceipt
    {
        internal ModuleUploadAdmission Issuer { get; } = issuer;
        internal ParentTransaction Parent { get; } = parent;
        internal ModuleUploadOperationRecord Operation { get; } = operation;
        public override ModuleUploadPolicyRef Policy => policy;
        public override Guid OperationId => Operation.OperationId;
        public override Guid SubjectId => Operation.SubjectId;
        public override string ObjectKey => Operation.ObjectKey;
        public override ObjectWriteIdentity Identity => ModuleUploadAdmission.Identity(Operation);
        public override DateTimeOffset ExpiresAtUtc => Operation.ExpiresAtUtc;
    }
}
