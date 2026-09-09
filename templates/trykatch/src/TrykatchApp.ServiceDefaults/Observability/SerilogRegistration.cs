using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace TrykatchApp.ServiceDefaults.Observability;

internal static class SerilogRegistration
{
    public static IServiceCollection AddTrykatchSerilog(this IServiceCollection services, ObservabilityPolicy policy, IConfiguration configuration)
    {
        services.AddSerilog((serviceProvider, logging) => logging
            .ReadFrom.Configuration(configuration)
            .ReadFrom.Services(serviceProvider)
            .Enrich.FromLogContext()
            .WriteTo.Sink(new SafeTelemetrySink(policy)), preserveStaticLogger: false, writeToProviders: false);
        return services;
    }
}
