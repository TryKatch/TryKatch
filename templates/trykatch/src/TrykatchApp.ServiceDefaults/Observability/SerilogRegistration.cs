using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;

namespace TrykatchApp.ServiceDefaults.Observability;

internal static class SerilogRegistration
{
    public static IServiceCollection AddTrykatchSerilog(this IServiceCollection services, ObservabilityPolicy policy)
    {
        services.AddSerilog(logging => logging
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Sink(new SafeTelemetrySink(policy)), preserveStaticLogger: false, writeToProviders: false);
        return services;
    }
}
