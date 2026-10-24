using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Storage;
using Trykatch.Application.Organizations;
using Trykatch.Application.Quotas;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Quotas;

/// <summary>One publication capability and receipt per actual enlisted transaction.</summary>
internal sealed class QuotaPublicationAuthority(ApplicationDbContext application,
    OrganizationControlPlaneDbContext control, IOrganizationContext organization, QuotaSettings settings)
{
    private Frame? current;
    private bool publishing;
    private bool resourceLocked;

    internal void EnsureChildAdmissionAllowed()
    {
        DbTransaction transaction = RequireTransaction();
        if (publishing || current is not null && ReferenceEquals(current.Transaction, transaction)
            && current.TransactionId == control.Database.CurrentTransaction!.TransactionId && resourceLocked)
            throw new InvalidOperationException("Upload admission must precede parent quota resource locks.");
    }

    internal async Task<QuotaPublicationReceipt> EnsurePublishedAsync(IOrganizationEntitlementProjectionPublisher publisher,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DbTransaction transaction = RequireTransaction();
        if (current is null || !ReferenceEquals(current.Transaction, transaction)
            || current.TransactionId != control.Database.CurrentTransaction!.TransactionId)
        {
            current = new(this, transaction, control.Database.CurrentTransaction!.TransactionId,
                organization.OrganizationId, organization.ActorId, settings);
            resourceLocked = false;
        }
        Frame frame = Validate(current);
        if (frame.Receipt is not null) return frame.Receipt;
        if (publishing || resourceLocked)
            throw new InvalidOperationException("Quota publication must complete before resource locks are acquired.");
        publishing = true;
        try
        {
            await publisher.PublishAsync(frame, cancellationToken);
            // The parent may have ended while the independent publisher awaited I/O.
            Validate(frame);
            return frame.Receipt ?? throw new OrganizationQuotaUnavailableException();
        }
        finally { publishing = false; }
    }

    internal Frame Validate(EntitlementPublication publication)
    {
        if (publication is not Frame frame || !ReferenceEquals(frame, current) || !ReferenceEquals(frame.Issuer, this)
            || !ReferenceEquals(frame.Settings, settings) || !ReferenceEquals(frame.Transaction, RequireTransaction())
            || frame.TransactionId != control.Database.CurrentTransaction!.TransactionId
            || frame.OrganizationId != organization.OrganizationId || frame.ActorId != organization.ActorId)
            throw new InvalidOperationException("Quota publication requires the current compiled host authority.");
        return frame;
    }

    internal void Complete(Frame frame, QuotaPublicationReceipt receipt)
    {
        Validate(frame);
        frame.Receipt = receipt;
    }

    internal void ResourceLockAcquired()
    {
        if (current is null || Validate(current).Receipt is null)
            throw new InvalidOperationException("Quota publication must complete before resource locks are acquired.");
        resourceLocked = true;
    }

    private DbTransaction RequireTransaction()
    {
        DbTransaction? transaction = application.Database.CurrentTransaction?.GetDbTransaction();
        if (!organization.IsResolved || organization.OrganizationId == Guid.Empty || organization.ActorId == Guid.Empty
            || transaction is null || transaction.Connection is null || transaction.IsolationLevel != IsolationLevel.ReadCommitted
            || !ReferenceEquals(transaction, control.Database.CurrentTransaction?.GetDbTransaction()))
            throw new InvalidOperationException("Quota publication requires the current enlisted organization transaction.");
        return transaction;
    }

    internal sealed class Frame(QuotaPublicationAuthority issuer, DbTransaction transaction, Guid transactionId, Guid organizationId,
        Guid actorId, QuotaSettings snapshot) : EntitlementPublication
    {
        internal QuotaPublicationAuthority Issuer { get; } = issuer;
        internal DbTransaction Transaction { get; } = transaction;
        internal Guid TransactionId { get; } = transactionId;
        internal Guid OrganizationId { get; } = organizationId;
        internal Guid ActorId { get; } = actorId;
        internal QuotaSettings Settings { get; } = snapshot;
        internal QuotaPublicationReceipt? Receipt { get; set; }
    }
}

internal sealed record QuotaPublicationReceipt(long Revision, string Hash)
{
    internal bool IsSatisfiedBy(OrganizationEntitlementRecord projection) => projection.Source == HostEntitlementPublisher.Source
        && (projection.SourceRevision > Revision || projection.SourceRevision == Revision && projection.SourceHash == Hash);
}
