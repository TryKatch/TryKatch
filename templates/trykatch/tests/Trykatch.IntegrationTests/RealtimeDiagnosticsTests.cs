using System.Diagnostics.Metrics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using Shouldly;
using StackExchange.Redis;
using Trykatch.Api.Realtime;
using Trykatch.ServiceDefaults.Observability;

namespace Trykatch.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class RealtimeDiagnosticsTests
{
    private const string MeterName = "Trykatch.Realtime";
    private const string InstrumentName = "trykatch.realtime.outcomes";
    private const string PasswordSentinel = "realtime-diagnostic-secret-sentinel";
    private const string UnreachableHost = "127.0.0.1";

    [TestMethod]
    public async Task DisabledAndLocalOnlyHealthChecksNeedNoBackplaneProbe()
    {
        foreach (RealtimeMode mode in new[] { RealtimeMode.Disabled, RealtimeMode.LocalOnly })
        {
            RealtimeHealthCheck check = new(Options.Create(new RealtimeOptions { Mode = mode }));
            HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());
            result.Status.ShouldBe(HealthStatus.Healthy);
        }
    }

    [TestMethod]
    public async Task DistributedHealthCheckDegradesWithoutExposingBackplaneOrProviderDetails()
    {
        ConfigurationOptions settings = RealtimeBackplaneSettings.Parse(
            $"{UnreachableHost}:1,password={PasswordSentinel}");
        await using RealtimeBackplaneProbe probe = new(settings);
        RealtimeHealthCheck check = new(Options.Create(new RealtimeOptions { Mode = RealtimeMode.Distributed }), probe);
        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext())
            .WaitAsync(TimeSpan.FromSeconds(5));

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Exception.ShouldBeNull();
        string diagnostics = string.Join("\n", new[] { result.Description ?? string.Empty }
            .Concat(result.Data.Select(pair => $"{pair.Key}={pair.Value}")));
        diagnostics.ShouldNotContain(PasswordSentinel);
        diagnostics.ShouldNotContain(UnreachableHost);
        diagnostics.ShouldNotContain("Redis");
        diagnostics.ShouldNotContain("SocketException");
    }

    [TestMethod]
    public async Task DistributedProbePropagatesCallerCancellationWithoutProviderDetails()
    {
        ConfigurationOptions settings = RealtimeBackplaneSettings.Parse(
            $"{UnreachableHost}:1,password={PasswordSentinel}");
        await using RealtimeBackplaneProbe probe = new(settings);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        OperationCanceledException exception = await Should.ThrowAsync<OperationCanceledException>(
            () => probe.CheckAsync(cancellation.Token));
        exception.ToString().ShouldNotContain(PasswordSentinel);
        exception.ToString().ShouldNotContain(UnreachableHost);
        exception.ToString().ShouldNotContain("Redis");
    }

    [TestMethod]
    public void RealtimeOutcomeMetricsUseClosedDimensionsAndRejectUndefinedEnums()
    {
        List<(string Meter, string Name, long Value, KeyValuePair<string, object?>[] Tags)> measurements = [];
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == MeterName) owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add((instrument.Meter.Name, instrument.Name, value, tags.ToArray())));
        listener.Start();

        foreach (RealtimeMode mode in Enum.GetValues<RealtimeMode>())
        foreach (RealtimeOutcome outcome in Enum.GetValues<RealtimeOutcome>())
            RealtimeMetrics.Record(mode, outcome);

        measurements.ShouldNotBeEmpty();
        foreach ((string meter, string name, _, KeyValuePair<string, object?>[] tags) in measurements)
        {
            meter.ShouldBe(MeterName);
            name.ShouldBe(InstrumentName);
            tags.Select(tag => tag.Key).Order(StringComparer.Ordinal).ShouldBe(["mode", "outcome"]);
            tags.Single(tag => tag.Key == "mode").Value.ShouldBeOneOf("disabled", "local", "distributed");
            tags.Single(tag => tag.Key == "outcome").Value.ShouldBeOneOf(
                "admitted", "rejected", "sent", "suppressed", "closed", "failed");
        }

        int beforeInvalid = measurements.Count;
        Should.Throw<ArgumentOutOfRangeException>(() => RealtimeMetrics.Record((RealtimeMode)999, RealtimeOutcome.Admitted));
        Should.Throw<ArgumentOutOfRangeException>(() => RealtimeMetrics.Record(RealtimeMode.LocalOnly, (RealtimeOutcome)999));
        measurements.Count.ShouldBe(beforeInvalid);

        using Meter meterSource = new(MeterName);
        Counter<long> outcomes = meterSource.CreateCounter<long>(InstrumentName);
        Counter<long> unexpected = meterSource.CreateCounter<long>("trykatch.realtime.unregistered");
        ObservabilityPolicy.MetricView(outcomes).TagKeys!.Order(StringComparer.Ordinal).ShouldBe(["mode", "outcome"]);
        ObservabilityPolicy.MetricView(unexpected).TagKeys.ShouldBeEmpty();
    }
}
