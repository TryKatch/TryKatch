using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Trykatch.Application.Features;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Features;

internal static class FeaturePolicyRegistration
{
    internal static void AddFeaturePolicy(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(provider => FeaturePolicyOptions.CreateSettings(configuration,
            provider.GetService<IHostEnvironment>()?.EnvironmentName ?? Environments.Production));
        services.AddHostedService<FeaturePolicyStartupValidator>();
        services.AddScoped<IFeatureControlStore>(provider => new FeatureControlStore(
            provider.GetRequiredService<PlatformDbContext>(), provider.GetRequiredService<TimeProvider>()));
        services.AddScoped<OrganizationFeatureEvaluator>();
        services.AddScoped<IOrganizationFeatureEvaluator>(provider => provider.GetRequiredService<OrganizationFeatureEvaluator>());
        services.AddScoped<IOrganizationFeatureSnapshotReader>(provider => provider.GetRequiredService<OrganizationFeatureEvaluator>());
    }

    private sealed class FeaturePolicyStartupValidator(FeaturePolicySettings settings) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = settings.Environment;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
