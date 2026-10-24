using System.Net;
using Trykatch.Application.Mailing;

namespace Trykatch.Infrastructure.Modules.Email;

/// <summary>Builds the established organization-invitation message without submitting it to SMTP.</summary>
internal sealed class InvitationEmailRendering(BrandedEmailTemplate template)
{
    public EmailPreparedMessage Render(string recipient, string organizationName, string roleName, string invitationUrl)
    {
        string safeOrganization = WebUtility.HtmlEncode(organizationName);
        string safeRole = WebUtility.HtmlEncode(roleName);
        return new EmailPreparedMessage(
            recipient,
            $"Join {organizationName} on {template.ApplicationName}",
            $"You have been invited to join {organizationName}.\n\nWorkspace role: {roleName}\n\nAccept the invitation and set up your account:\n{invitationUrl}\n\nThis invitation can be accepted once. If you were not expecting it, you can safely ignore this message.",
            template.Render("You're invited", $"Join {organizationName} as {roleName}.",
                $"<p>You have been invited to join <strong>{safeOrganization}</strong>.</p><p style=\"padding:16px;background-color:#f8faf9;border:1px solid #dce3e1;border-radius:4px;\">Workspace role: <strong>{safeRole}</strong></p><p>Accept the invitation and set up your account to get started.</p>",
                "Accept invitation", invitationUrl,
                "This invitation can be accepted once. If you were not expecting it, you can safely ignore this message."));
    }
}
