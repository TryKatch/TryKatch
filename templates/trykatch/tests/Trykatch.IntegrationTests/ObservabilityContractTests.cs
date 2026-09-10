using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Trace;
using Shouldly;

namespace Trykatch.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class ObservabilityContractTests
{
    [TestMethod]
    public void EndpointFreeDevelopmentHostStartsWithoutAnExporter()
    {
        using IHost host = BuildHost(new Dictionary<string, string?>(), Environments.Development);
        host.Services.GetRequiredService<TracerProvider>().ShouldNotBeNull();
    }

    [TestMethod]
    [DataRow("invalid")]
    [DataRow("http/json")]
    public void UnsupportedProtocolsFailWithoutLeakingHeaders(string protocol)
    {
        const string plantedSecret = "Bearer planted-secret-value";
        var values = ValidExportConfiguration();
        values["OTEL_EXPORTER_OTLP_PROTOCOL"] = protocol;
        values["OTEL_EXPORTER_OTLP_HEADERS"] = $"Authorization={Uri.EscapeDataString(plantedSecret)}";

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => BuildHost(values));
        exception.Message.ShouldContain("OTEL_EXPORTER_OTLP_PROTOCOL");
        exception.Message.ShouldNotContain(plantedSecret);
    }

    [TestMethod]
    public void ConflictingSignalEndpointFailsClosedWithoutLeakingItsValue()
    {
        var values = ValidExportConfiguration();
        values["OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"] = "https://do-not-print.example.invalid";

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => BuildHost(values));
        exception.Message.ShouldContain("conflicts with the common OTLP export configuration");
        exception.Message.ShouldNotContain("do-not-print");
    }

    [TestMethod]
    [DataRow("-0.01")]
    [DataRow("1.01")]
    [DataRow("not-a-number")]
    public void InvalidSamplingRatiosFailAtRegistration(string ratio)
    {
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => BuildHost(new Dictionary<string, string?>
        {
            ["OTEL_TRACES_SAMPLER"] = "parentbased_traceidratio",
            ["OTEL_TRACES_SAMPLER_ARG"] = ratio
        }));
        exception.Message.ShouldContain("OTEL_TRACES_SAMPLER_ARG");
    }

    [TestMethod]
    public void ParentBasedSamplerHonorsRootRatioAndRemoteParentDecision()
    {
        using IHost neverSampleHost = BuildHost(new Dictionary<string, string?>
        {
            ["OTEL_TRACES_SAMPLER"] = "parentbased_traceidratio",
            ["OTEL_TRACES_SAMPLER_ARG"] = "0"
        });
        TracerProvider provider = neverSampleHost.Services.GetRequiredService<TracerProvider>();
        using ActivitySource source = new("Trykatch.ObservabilityContract");

        using (Activity? droppedRoot = source.StartActivity("root"))
        {
            (droppedRoot?.Recorded ?? false).ShouldBeFalse();
        }
        ActivityContext sampledParent = new(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded, isRemote: true);
        using Activity? sampledChild = source.StartActivity("sampled-child", ActivityKind.Internal, sampledParent);
        sampledChild.ShouldNotBeNull();
        sampledChild.Recorded.ShouldBeTrue();

        ActivityContext unsampledParent = new(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.None, isRemote: true);
        using (Activity? unsampledChild = source.StartActivity("unsampled-child", ActivityKind.Internal, unsampledParent))
        {
            (unsampledChild?.Recorded ?? false).ShouldBeFalse();
        }
        provider.ForceFlush(1000).ShouldBeTrue();

        using IHost alwaysSampleHost = BuildHost(new Dictionary<string, string?>
        {
            ["OTEL_TRACES_SAMPLER"] = "parentbased_traceidratio",
            ["OTEL_TRACES_SAMPLER_ARG"] = "1"
        });
        alwaysSampleHost.Services.GetRequiredService<TracerProvider>();
        using ActivitySource alwaysSource = new("Trykatch.ObservabilityContract");
        using Activity? root = alwaysSource.StartActivity("root");
        root.ShouldNotBeNull();
        root.Recorded.ShouldBeTrue();
    }

    [TestMethod]
    public void ResourceAttributeAllowlistRejectsUnexpectedIdentityData()
    {
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => BuildHost(new Dictionary<string, string?>
        {
            ["OTEL_RESOURCE_ATTRIBUTES"] = "service.name=api,user.email=private%40example.com"
        }));
        exception.Message.ShouldContain("unsupported attribute 'user.email'");
        exception.Message.ShouldNotContain("private@example.com");
    }

    private static IHost BuildHost(Dictionary<string, string?> values, string environment = "Production")
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "Trykatch.ObservabilityContract",
            EnvironmentName = environment
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(values);
        builder.AddServiceDefaults();
        return builder.Build();
    }

    private static Dictionary<string, string?> ValidExportConfiguration() => new()
    {
        ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:4318",
        ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf"
    };
}
