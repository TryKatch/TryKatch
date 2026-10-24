using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Trykatch.Application.Authorization;
using Trykatch.Application.Mailing;
using Trykatch.Application.Organizations;
using Trykatch.Infrastructure.Mailing;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Modules.Email;

/// <summary>Appends the core invitation intent to its existing invitation/audit transaction.</summary>
internal sealed class OrganizationInvitationEmailQueue(IOrganizationContext organization,
    OrganizationControlPlaneDbContext control, OrganizationManagementAuthorization authorization,
    OrganizationInvitationEmailPolicy policy, InvitationEmailLinkPolicy links, OrganizationEmailDeliveryStore store,
    IEmailPayloadProtector protector, TimeProvider clock) : IOrganizationInvitationEmailSubmission
{
    private DbTransaction? pendingTransaction;
    private readonly Dictionary<Guid, (InvitationEmailIntent Intent, DateTimeOffset Created)> pending = [];
    public bool IsConfigured => links.IsAvailable;

    public async Task<ModuleWorkReceipt> EnqueueAsync(OrganizationInvitationEmailRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsConfigured) return new(Guid.Empty, ModuleWorkState.Unavailable, FailureCode: "email_unavailable");
        if (!organization.IsResolved || request.InvitationId == Guid.Empty || !InvitationEmailIntentCodec.IsToken(request.Token))
            return Rejected("invalid_request");
        try { if (organization.MembershipId == Guid.Empty) return Rejected("not_authorized"); }
        catch (OrganizationMembershipRequiredException) { return Rejected("not_authorized"); }
        IDbContextTransaction transaction = control.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Invitation email submission requires the active organization transaction.");
        if (control.Database.CreateExecutionStrategy().RetriesOnFailure)
            throw new InvalidOperationException("Invitation email submission cannot retry the business transaction.");
        var authority = await authorization.BeginAsync(Permissions.MembersManage, cancellationToken);
        if (!authority.IsSuccess) return Rejected("not_authorized");
        var invitation = await control.Invitations.AsNoTracking().SingleOrDefaultAsync(candidate =>
            candidate.Id == request.InvitationId && candidate.OrganizationId == organization.OrganizationId, cancellationToken);
        if (invitation is null || !authority.Value!.CanManageInvitation(invitation)
            || !OrganizationModuleEmailIntentCodec.IsRecipient(invitation.Email))
            return Rejected("source_denied");
        InvitationEmailIntent intent = new(1, Guid.CreateVersion7(), organization.OrganizationId, organization.ActorId,
            invitation.Id, invitation.RoleId, invitation.Email, request.Token, links.Build(request.Token));
        EmailDeliveryPreparation current = await policy.PrepareAsync(intent, cancellationToken);
        if (current.Disposition != EmailPreparationDisposition.Ready) return Rejected("source_denied");
        DbTransaction currentTransaction = transaction.GetDbTransaction();
        if (!ReferenceEquals(pendingTransaction, currentTransaction))
        {
            pending.Clear();
            pendingTransaction = currentTransaction;
        }
        if (pending.TryGetValue(invitation.Id, out var accepted))
            return InvitationEmailIntentCodec.SameIntent(accepted.Intent, intent)
                ? new(accepted.Intent.DeliveryId, ModuleWorkState.AlreadyAccepted, accepted.Created) : Rejected("request_id_conflict");
        // BeginAsync holds the organization management lock, including duplicate admission across callers.
        OrganizationEmailDeliveryRecord? existing = await store.FindByAdmissionAsync(intent.OrganizationId,
            InvitationEmailIntentCodec.AdmissionKey, InvitationEmailIntentCodec.TemplateKey, InvitationEmailIntentCodec.TemplateVersion,
            intent.InvitationId, cancellationToken);
        if (existing is not null)
            return InvitationEmailIntentCodec.TryRead(existing, protector, out InvitationEmailIntent? previous)
                && InvitationEmailIntentCodec.SameIntent(previous!, intent)
                ? new(existing.Id, ModuleWorkState.AlreadyAccepted, existing.CreatedAtUtc) : Rejected("request_id_conflict");
        string protectedIntent = InvitationEmailIntentCodec.Protect(intent, protector);
        if (protectedIntent.Length > 262_144) return Rejected("invalid_payload");
        DateTimeOffset created = clock.GetUtcNow().ToUniversalTime();
        await control.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO infrastructure.email_deliveries
                ("Id", "OrganizationId", "ActorId", "MessageId", "AdmissionIdentityKey", "TemplateKey", "TemplateVersion",
                 "ProtectedIntent", "State", "Attempts", "CreatedAtUtc")
            VALUES ({intent.DeliveryId}, {intent.OrganizationId}, {intent.ActorId}, {intent.InvitationId},
                {InvitationEmailIntentCodec.AdmissionKey}, {InvitationEmailIntentCodec.TemplateKey}, {InvitationEmailIntentCodec.TemplateVersion},
                {protectedIntent}, 'queued', 0, {created})
            """, cancellationToken);
        pending.Add(invitation.Id, (intent, created));
        return new(intent.DeliveryId, ModuleWorkState.Accepted, created);
    }

    private static ModuleWorkReceipt Rejected(string code) => new(Guid.Empty, ModuleWorkState.Rejected, FailureCode: code);
}
