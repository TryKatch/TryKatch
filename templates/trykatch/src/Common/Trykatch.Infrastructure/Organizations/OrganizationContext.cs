using System.Collections.Frozen;
using Trykatch.Application.Organizations;

namespace Trykatch.Infrastructure.Organizations;

public interface IOrganizationContextInitializer
{
    void Initialize(OrganizationAccess access);
}

internal sealed record OrganizationSystemAccess(
    Guid OrganizationId, string OrganizationSlug, Guid ActorId, IReadOnlySet<string> Permissions);

internal sealed class OrganizationContext : IOrganizationContext, IOrganizationContextInitializer
{
    private OrganizationAccess? access;
    private OrganizationSystemAccess? system;

    public bool IsResolved => access is not null || system is not null;
    public Guid OrganizationId => system?.OrganizationId ?? GetUser().OrganizationId;
    public string OrganizationSlug => system?.OrganizationSlug ?? GetUser().OrganizationSlug;
    public Guid ActorId => system?.ActorId ?? GetUser().ActorId;
    public Guid MembershipId => system is not null
        ? throw new OrganizationMembershipRequiredException()
        : GetUser().MembershipId;
    public IReadOnlySet<string> Permissions => system?.Permissions ?? GetUser().Permissions;

    public void Initialize(OrganizationAccess value)
    {
        ArgumentNullException.ThrowIfNull(value);
        EnsureUnresolved();
        access = value;
    }

    // Only the host executor may establish this principal after validating a finite installed grant.
    internal void InitializeSystem(OrganizationSystemAccess value)
    {
        ArgumentNullException.ThrowIfNull(value);
        EnsureUnresolved();
        system = value with { Permissions = value.Permissions.ToFrozenSet(StringComparer.Ordinal) };
    }

    private void EnsureUnresolved()
    {
        if (IsResolved)
            throw new InvalidOperationException("Organization context can only be initialized once per scope.");
    }

    private OrganizationAccess GetUser() =>
        access ?? throw new InvalidOperationException("Organization context has not been resolved.");
}
