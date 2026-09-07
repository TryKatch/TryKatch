using FlatpackApp.Application.Organizations;

namespace FlatpackApp.Infrastructure.Organizations;

public interface IOrganizationContextInitializer
{
    void Initialize(OrganizationAccess access);
}

internal sealed class OrganizationContext : IOrganizationContext, IOrganizationContextInitializer
{
    private OrganizationAccess? _access;

    public bool IsResolved => _access is not null;
    public Guid OrganizationId => Get().OrganizationId;
    public string OrganizationSlug => Get().OrganizationSlug;
    public Guid ActorId => Get().ActorId;
    public Guid MembershipId => Get().MembershipId;
    public IReadOnlySet<string> Permissions => Get().Permissions;

    public void Initialize(OrganizationAccess access)
    {
        if (_access is not null)
        {
            throw new InvalidOperationException("Organization context can only be initialized once per request.");
        }

        _access = access;
    }

    private OrganizationAccess Get() =>
        _access ?? throw new InvalidOperationException("Organization context has not been resolved.");
}

