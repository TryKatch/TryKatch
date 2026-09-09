using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TrykatchApp.ServiceDefaults.Observability;

namespace Microsoft.Extensions.Hosting;

public static class Extensions
{
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        ObservabilityPolicy policy = ObservabilityPolicy.Resolve(builder.Configuration, builder.Environment);
        builder.Services.AddSingleton(policy);
        builder.Services.AddTrykatchSerilog(policy, builder.Configuration);

        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler();
            http.AddServiceDiscovery();
        });

        builder.Services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.Clear().AddAttributes(policy.ResourceAttributes))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddNpgsqlInstrumentation()
                .AddMeter("TrykatchApp.Outbox")
                .AddView(instrument => ObservabilityPolicy.MetricView(instrument)))
            .WithTracing(traces => traces
                .SetSampler(policy.Sampler)
                .AddSource(builder.Environment.ApplicationName, "TrykatchApp.Outbox")
                .AddAspNetCoreInstrumentation(options => options.Filter = context =>
                    !OperationalRoutePolicy.IsOperationalPath(context.Request.Path))
                .AddHttpClientInstrumentation()
                .AddNpgsql());

        if (policy.ExportEnabled)
        {
            builder.Services.ConfigureOpenTelemetryTracerProvider(traces => traces.AddOtlpExporter(options =>
            {
                options.Endpoint = policy.Endpoint!;
                options.Protocol = policy.Protocol;
                options.Headers = policy.HeadersText;
            }));
            builder.Services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddOtlpExporter(options =>
            {
                options.Endpoint = policy.Endpoint!;
                options.Protocol = policy.Protocol;
                options.Headers = policy.HeadersText;
            }));
        }

        return builder;
    }

    public static IApplicationBuilder UseServiceDefaults(this IApplicationBuilder app) =>
        app.UseMiddleware<SafeRequestLoggingMiddleware>();

    public static IEndpointRouteBuilder MapDefaultEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("live")
        }).DisableHttpMetrics();
        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = registration => !registration.Tags.Contains("live")
        }).DisableHttpMetrics();
        return endpoints;
    }
}
