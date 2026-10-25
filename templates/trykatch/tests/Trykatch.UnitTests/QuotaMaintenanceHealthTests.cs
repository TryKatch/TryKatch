using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shouldly;
using Trykatch.Infrastructure.Quotas;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class QuotaMaintenanceHealthTests
{
    private static readonly string[] SafeKeys = ["oldestAgeSeconds", "pausedCount", "pendingCount", "retryingCount"];
    [TestMethod]
    public async Task MaintenanceIsUnavailableUntilTheFirstSuccessfulObservation()
    {
        QuotaMaintenanceWorkerState state = new();
        HealthCheckResult result = await new QuotaMaintenanceHealthCheck(state, TimeProvider.System)
            .CheckHealthAsync(new());
        result.Status.ShouldBe(HealthStatus.Unhealthy);
    }

    [TestMethod]
    public async Task HealthyMaintenanceReportsOnlySafeAggregateBacklog()
    {
        QuotaMaintenanceWorkerState state = new();
        state.Observe(new(2, 0, 0, DateTimeOffset.UtcNow));
        HealthCheckResult result = await new QuotaMaintenanceHealthCheck(state, TimeProvider.System)
            .CheckHealthAsync(new());
        result.Status.ShouldBe(HealthStatus.Healthy);
        result.Data.Keys.Order().ShouldBe(SafeKeys);
        result.Data["pendingCount"].ShouldBe(2L);
    }

    [TestMethod]
    [DataRow("paused")]
    [DataRow("retrying")]
    [DataRow("old")]
    public async Task RetainedWorkRequiringAttentionDegradesMaintenanceHealth(string condition)
    {
        QuotaMaintenanceWorkerState state = new();
        state.Observe(new(1, condition == "retrying" ? 1 : 0, condition == "paused" ? 1 : 0,
            DateTimeOffset.UtcNow - (condition == "old" ? TimeSpan.FromMinutes(10) : TimeSpan.Zero)));
        HealthCheckResult result = await new QuotaMaintenanceHealthCheck(state, TimeProvider.System)
            .CheckHealthAsync(new());
        result.Status.ShouldBe(HealthStatus.Degraded);
    }

    [TestMethod]
    public async Task SuccessfulObservationClearsCoordinationFailureWithoutDiscardingBacklog()
    {
        QuotaMaintenanceWorkerState state = new();
        state.Observe(new(3, 1, 0, DateTimeOffset.UtcNow));
        state.Unavailable();
        QuotaMaintenanceHealthCheck check = new(state, TimeProvider.System);
        (await check.CheckHealthAsync(new())).Status.ShouldBe(HealthStatus.Unhealthy);
        state.Observe(new(3, 0, 0, DateTimeOffset.UtcNow));
        HealthCheckResult recovered = await check.CheckHealthAsync(new());
        recovered.Status.ShouldBe(HealthStatus.Healthy);
        recovered.Data["pendingCount"].ShouldBe(3L);
    }
}
