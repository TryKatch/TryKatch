namespace Trykatch.Application.Quotas;

/// <summary>A host-issued publication capability. Application input cannot supply its authority.</summary>
public abstract class EntitlementPublication
{
    protected EntitlementPublication() { }
}

/// <summary>Publishes only compiled, validated host capabilities; this is not an allowance editing API.</summary>
public interface IOrganizationEntitlementProjectionPublisher
{
    Task PublishAsync(EntitlementPublication publication, CancellationToken cancellationToken = default);
}
