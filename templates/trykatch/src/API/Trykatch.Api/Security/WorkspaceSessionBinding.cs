using Microsoft.AspNetCore.Authentication;
using Trykatch.Application.Identity;

namespace Trykatch.Api.Security;

/// <summary>Rotates the durable browser generation before a workspace cookie changes.</summary>
public sealed class WorkspaceSessionBinding(IAccountSessions sessions, ICurrentAccountSession current)
{
    public async Task<bool> RotateAsync(HttpContext context, Guid? organizationId, Guid? expectedActorId = null)
    {
        AccountSessionProof? proof = current.Proof;
        if (proof is null)
        {
            // Anonymous clearing and existing bearer-only HTTP flows have no application-cookie session.
            return !context.User.Identities.Any(identity => identity.IsAuthenticated
                && identity.AuthenticationType == Trykatch.Identity.AuthenticationSchemes.ApplicationCookie);
        }
        if (expectedActorId.HasValue && proof.ActorId != expectedActorId.Value) return false;
        if (await sessions.BindWorkspaceAsync(proof, organizationId, context.RequestAborted) is not null) return true;
        await context.SignOutAsync(Trykatch.Identity.AuthenticationSchemes.ApplicationCookie);
        return false;
    }
}
