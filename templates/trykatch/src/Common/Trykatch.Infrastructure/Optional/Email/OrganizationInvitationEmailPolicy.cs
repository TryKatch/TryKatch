using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Trykatch.Application.Mailing;
using Trykatch.Domain.Common;
using Trykatch.Domain.Organizations;
using Trykatch.Infrastructure.Persistence;

namespace Trykatch.Infrastructure.Modules.Email;

internal sealed class OrganizationInvitationEmailPolicy(OrganizationControlPlaneDbContext control,
    InvitationEmailLinkPolicy links, InvitationEmailRendering rendering, TimeProvider clock)
{
    public async Task<EmailDeliveryPreparation> PrepareAsync(InvitationEmailIntent intent, CancellationToken cancellationToken)
    {
        if (control.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Invitation email disclosure requires the organization transaction.");
        if (!links.IsValid(intent.InvitationUrl, intent.Token)) return Denied(EmailFailureCode.InvalidConfiguration);
        Invitation? invitation = await control.Invitations.AsNoTracking().SingleOrDefaultAsync(candidate =>
            candidate.Id == intent.InvitationId && candidate.OrganizationId == intent.OrganizationId, cancellationToken);
        if (invitation is null || !invitation.IsUsable(clock.GetUtcNow()) || invitation.RoleId != intent.RoleId
            || invitation.TokenHash != Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(intent.Token))))
            return Denied(EmailFailureCode.TokenInvalid);
        if (!string.Equals(invitation.Email, intent.Recipient, StringComparison.OrdinalIgnoreCase))
            return Denied(EmailFailureCode.RecipientChanged);
        Role? role = await control.Roles.AsNoTracking().SingleOrDefaultAsync(candidate =>
            candidate.Id == invitation.RoleId && candidate.OrganizationId == intent.OrganizationId, cancellationToken);
        Organization? organization = await control.Organizations.AsNoTracking().SingleOrDefaultAsync(candidate =>
            candidate.Id == intent.OrganizationId && candidate.IsActive, cancellationToken);
        if (organization is null || role is null || role.LifecycleState != RecordLifecycleState.Active)
            return Denied(EmailFailureCode.SourceDenied);
        EmailPreparedMessage message = rendering.Render(invitation.Email, organization.Name, role.Name, intent.InvitationUrl);
        if (message.Subject.Length is 0 or > 240 || message.Subject.Any(char.IsControl)
            || Encoding.UTF8.GetByteCount(message.TextBody) + Encoding.UTF8.GetByteCount(message.HtmlBody ?? "") > 262_144)
            return Denied(EmailFailureCode.PayloadInvalid);
        return new(EmailPreparationDisposition.Ready, message);
    }

    private static EmailDeliveryPreparation Denied(EmailFailureCode code) => new(EmailPreparationDisposition.Suppressed, FailureCode: code);
}
