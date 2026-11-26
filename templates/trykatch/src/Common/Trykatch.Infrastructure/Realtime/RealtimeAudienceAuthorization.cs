using Trykatch.Application.Organizations;
using Trykatch.Application.Realtime;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Realtime;

internal sealed class RealtimeAudienceAuthorization(RealtimeAudienceRegistry registry, IOrganizationContext organization)
    : IRealtimeAudienceAuthorization
{
    public async Task<bool> AuthorizeAsync(ModuleRealtimeAudience audience, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!HasHumanScope()) return false;
        try { ModuleRealtimeRegistrationRules.ValidateAudience(audience); }
        catch (InvalidOperationException) { return false; }
        if (!registry.TryGet(audience.Policy, out RealtimeAudienceRegistration? registration) || registration is null
            || !registration.Descriptor.AllowedAudiences.Contains(audience.Kind)
            || audience.Resource is not null
                && !registration.Descriptor.AllowedResourceKeys.Contains(audience.Resource.ResourceKey, StringComparer.Ordinal)
            || audience.Kind == ModuleRealtimeAudienceKind.User && audience.UserId != organization.ActorId
            || registration.Descriptor.RequiredUserPermissionKeys.Any(permission => !organization.Permissions.Contains(permission)))
            return false;
        // Every attempt, including organization/user resource hints, must await the current module policy.
        return await registration.Adapter.AuthorizeAsync(new(organization.OrganizationId, organization.ActorId), audience, cancellationToken);
    }

    public Task<bool> AuthorizeAsync(ModuleRealtimeNotification notification, CancellationToken cancellationToken = default)
    {
        try { registry.RequireNotification(notification); }
        catch (InvalidOperationException) { return Task.FromResult(false); }
        catch (ArgumentNullException) { return Task.FromResult(false); }
        return AuthorizeAsync(notification.Audience, cancellationToken);
    }

    private bool HasHumanScope()
    {
        if (!organization.IsResolved || organization.OrganizationId == Guid.Empty || organization.ActorId == Guid.Empty) return false;
        try { return organization.MembershipId != Guid.Empty; }
        catch (OrganizationMembershipRequiredException) { return false; }
    }
}
