using Trykatch.Modules;

namespace Trykatch.Application.Mailing;

/// <summary>Core invitation reference; organization and actor come from the authenticated host scope.</summary>
public sealed record OrganizationInvitationEmailRequest(Guid InvitationId, string Token)
{
    public override string ToString() => $"Invitation email request {InvitationId:N}";
}

public interface IOrganizationInvitationEmailSubmission
{
    bool IsConfigured { get; }
    Task<ModuleWorkReceipt> EnqueueAsync(OrganizationInvitationEmailRequest request, CancellationToken cancellationToken);
}
