using Trykatch.Application.Mailing;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Mailing;

internal sealed class UnavailableOrganizationInvitationEmailSubmission : IOrganizationInvitationEmailSubmission
{
    public bool IsConfigured => false;
    public Task<ModuleWorkReceipt> EnqueueAsync(OrganizationInvitationEmailRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ModuleWorkReceipt(Guid.Empty, ModuleWorkState.Unavailable, FailureCode: "email_unavailable"));
    }
}
