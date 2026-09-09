using TrykatchApp.Application.Identity;

namespace TrykatchApp.Infrastructure.Identity;

internal sealed class NoOpInvitationNotifier : IInvitationNotifier
{
    public bool IsConfigured => false;

    public Task SendOrganizationInvitationAsync(
        string recipient,
        string organizationName,
        string invitationUrl,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}
