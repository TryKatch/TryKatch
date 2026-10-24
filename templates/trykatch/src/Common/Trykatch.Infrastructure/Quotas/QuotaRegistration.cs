using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Trykatch.Application.Quotas;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Quotas;

internal static class QuotaRegistration
{
    internal static void AddOrganizationQuotas(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(provider => QuotaOptions.CreateSettings(configuration, provider.GetRequiredService<ModuleCatalog>()));
        services.AddHostedService<QuotaStartupValidator>();
        services.AddScoped<QuotaPublicationAuthority>();
        services.AddScoped<QuotaWorkflowRegistry>();
        services.AddScoped<IOrganizationEntitlementProjectionPublisher, HostEntitlementPublisher>();
        services.AddScoped<OrganizationQuotaService>();
        services.AddScoped<IOrganizationQuota>(provider => provider.GetRequiredService<OrganizationQuotaService>());
        services.AddScoped<IOrganizationEntitlementReader>(provider => provider.GetRequiredService<OrganizationQuotaService>());
    }

    private sealed class QuotaStartupValidator(QuotaSettings settings, IServiceScopeFactory scopes) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = settings.Hash;
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            _ = scope.ServiceProvider.GetRequiredService<QuotaWorkflowRegistry>();
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
