using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Trykatch.Application.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Idempotency;

internal sealed class OrganizationRequestStore(
    ApplicationDbContext application,
    OrganizationControlPlaneDbContext controlPlane,
    IOrganizationContext organization,
    IOrganizationAccessResolver accessResolver,
    ModuleCatalog catalog,
    TimeProvider timeProvider) : IModuleMutationIdempotencyStore
{
    private bool attempted;
    private Reservation? reservation;

    public async Task<MutationReservationResult> ReserveAsync(MutationRequest request,
        Func<MutationAuthority, CancellationToken, Task<bool>> authorizeRequest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(authorizeRequest);
        request.Validate();
        request = request with { RequiredPermissions = request.RequiredPermissions.ToArray() };
        if (attempted) throw new InvalidOperationException("A scoped store accepts only one mutation reservation attempt.");
        attempted = true;
        DbTransaction transaction = RequireTransaction();
        cancellationToken.ThrowIfCancellationRequested();
        if (!organization.IsResolved || !catalog.Contains(request.ModuleId)) return Denied();
        Guid membershipId;
        try { membershipId = organization.MembershipId; }
        catch (OrganizationMembershipRequiredException) { return Denied(); }
        Guid organizationId = organization.OrganizationId;
        Guid actorId = organization.ActorId;
        if (organizationId == Guid.Empty || actorId == Guid.Empty || membershipId == Guid.Empty) return Denied();

        // The request hash is deliberately absent: changed intent must compete for the same identity.
        string identity = $"trykatch.mutation.v1:{organizationId:D}:{actorId:D}:{request.ModuleId.Length}:{request.ModuleId}:{request.OperationId.Length}:{request.OperationId}:{request.KeyHash}";
        long lockKey = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        bool locked = await application.Database.SqlQuery<bool>(
            $"SELECT pg_try_advisory_xact_lock({lockKey}) AS \"Value\"").SingleAsync(cancellationToken);

        // Reauthorize even when the lock is busy, before disclosing that an identity exists.
        OrganizationAccess? fresh = await accessResolver.ResolveAsync(actorId, organizationId, cancellationToken);
        if (fresh is null || fresh.OrganizationId != organizationId || fresh.ActorId != actorId
            || fresh.MembershipId != membershipId || !request.RequiredPermissions.All(fresh.Permissions.Contains))
            return Denied();
        MutationAuthority authority = new(organizationId, actorId, membershipId,
            fresh.Permissions.ToFrozenSet(StringComparer.Ordinal));
        if (!await authorizeRequest(authority, cancellationToken)) return Denied();
        cancellationToken.ThrowIfCancellationRequested();
        if (!locked) return new(MutationReservationState.InProgress);

        MutationReceiptRecord? stored = await application.Set<MutationReceiptRecord>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.OrganizationId == organizationId && row.ActorId == actorId
                && row.ModuleId == request.ModuleId && row.OperationId == request.OperationId && row.KeyHash == request.KeyHash,
                cancellationToken);
        DateTimeOffset now = UtcNow();
        if (stored is not null && stored.ExpiresAtUtc > now && stored.CompletedAtUtc + request.Policy.ReplayLifetime > now)
        {
            if (!string.Equals(stored.RequestHash, request.RequestHash, StringComparison.Ordinal))
                return new(MutationReservationState.Conflict);
            MutationReceipt receipt = new(stored.StatusCode, stored.ContentType, stored.Location, stored.Body,
                stored.ResourceKey is { } resourceKey && stored.ResourceId is { } resourceId
                    ? new(resourceKey, resourceId) : null);
            try { receipt.Validate(request.Policy); }
            catch (ArgumentException) { return Denied(); }
            return new(MutationReservationState.Completed, authority, Receipt: receipt);
        }

        Guid token = Guid.NewGuid();
        reservation = new(token, transaction, request, authority);
        return new(MutationReservationState.Reserved, authority, token);
    }

    public async Task CompleteAsync(Guid reservationId, MutationReceipt receipt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        Reservation current = reservation
            ?? throw new InvalidOperationException("No active mutation reservation exists in this scope.");
        if (current.Id != reservationId || !ReferenceEquals(current.Transaction, RequireTransaction()))
            throw new InvalidOperationException("The mutation reservation belongs to a different scope or transaction.");
        receipt.Validate(current.Request.Policy);
        receipt = receipt with { Body = receipt.Body.ToArray() };
        cancellationToken.ThrowIfCancellationRequested();
        // Once persistence starts, a failed attempt must roll back rather than retry inside this transaction.
        reservation = null;
        DateTimeOffset completed = UtcNow();
        DateTimeOffset expires = completed + current.Request.Policy.ReplayLifetime;
        DateTimeOffset cutoff = completed - current.Request.Policy.ReplayLifetime;
        MutationRequest request = current.Request;
        MutationAuthority authority = current.Authority;
        int affected = await application.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO app.mutation_receipts
              ("OrganizationId", "ActorId", "ModuleId", "OperationId", "KeyHash", "RequestHash",
               "StatusCode", "ContentType", "Location", "Body", "ResourceKey", "ResourceId", "CompletedAtUtc", "ExpiresAtUtc")
            VALUES ({authority.OrganizationId}, {authority.ActorId}, {request.ModuleId}, {request.OperationId}, {request.KeyHash}, {request.RequestHash},
                    {receipt.StatusCode}, {receipt.ContentType}, {receipt.Location}, {receipt.Body},
                    {receipt.ResourceReference?.ResourceKey}, {receipt.ResourceReference?.ResourceId}, {completed}, {expires})
            ON CONFLICT ("OrganizationId", "ActorId", "ModuleId", "OperationId", "KeyHash") DO UPDATE
            SET "RequestHash" = EXCLUDED."RequestHash", "StatusCode" = EXCLUDED."StatusCode",
                "ContentType" = EXCLUDED."ContentType", "Location" = EXCLUDED."Location", "Body" = EXCLUDED."Body",
                "ResourceKey" = EXCLUDED."ResourceKey", "ResourceId" = EXCLUDED."ResourceId",
                "CompletedAtUtc" = EXCLUDED."CompletedAtUtc", "ExpiresAtUtc" = EXCLUDED."ExpiresAtUtc"
            WHERE app.mutation_receipts."ExpiresAtUtc" <= {completed} OR app.mutation_receipts."CompletedAtUtc" <= {cutoff}
            """, cancellationToken);
        if (affected != 1) throw new InvalidOperationException("An unexpired mutation receipt cannot be replaced.");
    }

    private DbTransaction RequireTransaction()
    {
        DbTransaction? applicationTransaction = application.Database.CurrentTransaction?.GetDbTransaction();
        DbTransaction? controlTransaction = controlPlane.Database.CurrentTransaction?.GetDbTransaction();
        if (applicationTransaction is null || !ReferenceEquals(applicationTransaction, controlTransaction)
            || applicationTransaction.Connection is null || applicationTransaction.IsolationLevel != IsolationLevel.ReadCommitted)
            throw new InvalidOperationException("Mutation idempotency requires the same active ReadCommitted organization transaction.");
        return applicationTransaction;
    }

    private DateTimeOffset UtcNow()
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        // PostgreSQL stores microseconds; use the same boundary for expiry comparisons and writes.
        return new(now.UtcTicks / 10 * 10, TimeSpan.Zero);
    }

    private static MutationReservationResult Denied() => new(MutationReservationState.Denied);

    private sealed record Reservation(Guid Id, DbTransaction Transaction, MutationRequest Request, MutationAuthority Authority);
}
