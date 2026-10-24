using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Trykatch.Application.Mailing;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Modules.Email;

/// <summary>Starts queued delivery only when a runtime host explicitly composed a delivery source.</summary>
public static class QueuedEmailHosting
{
    public static IServiceCollection AddQueuedEmailDelivery(this IServiceCollection services, bool isOpenApiGeneration)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (isOpenApiGeneration || !services.Any(descriptor => descriptor.ServiceType == typeof(IEmailDeliverySource)))
            return services;

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<QueuedEmailDispatcher>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, QueuedEmailDeliveryStartupValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, QueuedEmailDeliveryWorker>());
        return services;
    }
}

internal sealed class QueuedEmailDeliveryStartupValidator(IServiceScopeFactory scopes) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IEmailDeliverySource[] sources = scope.ServiceProvider.GetServices<IEmailDeliverySource>().ToArray();
        HashSet<EmailDeliveryBoundary> boundaries = [];
        foreach (IEmailDeliverySource source in sources)
        {
            if (source is null || !Enum.IsDefined(source.Boundary) || !boundaries.Add(source.Boundary))
                throw new InvalidOperationException("Email delivery sources must use unique supported boundaries.");
        }

        _ = scope.ServiceProvider.GetService<EmailTemplateRegistry>();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
