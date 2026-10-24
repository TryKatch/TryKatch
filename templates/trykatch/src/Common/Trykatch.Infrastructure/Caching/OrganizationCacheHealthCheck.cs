using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Trykatch.Infrastructure.Caching;

internal sealed class OrganizationCacheHealthCheck(
    IOptions<OrganizationCacheOptions> options,
    [FromKeyedServices(OrganizationCacheRegistration.DistributedServiceKey)] IDistributedCache backend) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (options.Value.Mode == OrganizationCacheMode.Disabled)
            return HealthCheckResult.Healthy("Organization caching is disabled.");
        if (options.Value.Mode == OrganizationCacheMode.LocalOnly)
            return HealthCheckResult.Healthy("Organization caching uses the local process.");

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            // A miss is sufficient to test read connectivity. No tenant data, key creation,
            // authority lookup or generation mutation belongs in this optional adapter probe.
            _ = await backend.GetAsync("health:read-probe:v1", deadline.Token).ConfigureAwait(false);
            return HealthCheckResult.Healthy("Shared organization cache read probe succeeded.");
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Caching is optional: source reads remain available. Never publish endpoint,
            // credential, key or vendor exception details in the operational response.
            return HealthCheckResult.Degraded("Shared organization cache is unavailable; source reads remain enabled.");
        }
    }
}
