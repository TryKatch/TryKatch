namespace Trykatch.Application.Identity;

public interface IInvitationNotifier
{
    bool IsConfigured { get; }

    Task SendOrganizationInvitationAsync(
        string recipient,
        string organizationName,
        string roleName,
        string invitationUrl,
        CancellationToken cancellationToken = default);
}
