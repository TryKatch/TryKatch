using Trykatch.Application.Auditing;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Shouldly;
using Trykatch.Infrastructure.Persistence;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class AuditIntentProjectorTests
{
    [TestMethod]
    public async Task CommittedIntentIsProjectedExactlyOnceAcrossReplay()
    {
        MemoryProjectionStore store = new([CreateIntent()]);
        AuditIntentProjector projector = new(store);

        AuditProjectionBatch first = await projector.ProjectBatchAsync(10, CancellationToken.None);
        AuditProjectionBatch replay = await projector.ProjectBatchAsync(10, CancellationToken.None);

        first.ShouldBe(new AuditProjectionBatch(1, 1));
        replay.ShouldBe(new AuditProjectionBatch(0, 0));
        store.ProjectedIds.ShouldBe([store.IntentId]);
    }

    [TestMethod]
    public async Task ProjectionFailureLeavesCommittedIntentAvailableForRetry()
    {
        MemoryProjectionStore store = new([CreateIntent()]) { FailNextProjection = true };
        AuditIntentProjector projector = new(store);

        await Should.ThrowAsync<InvalidOperationException>(() => projector.ProjectBatchAsync(10, CancellationToken.None));
        store.ProjectedIds.ShouldBeEmpty();

        (await projector.ProjectBatchAsync(10, CancellationToken.None)).Projected.ShouldBe(1);
        store.ProjectedIds.ShouldBe([store.IntentId]);
    }

    [TestMethod]
    public async Task ProjectionFailureStillPublishesOldestBacklogSnapshotAndDegradesHealth()
    {
        DateTimeOffset oldest = new(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);
        FixedTimeProvider clock = new(oldest.AddMinutes(3));
        MemoryProjectionStore store = new([CreateIntent(oldest)]) { FailNextProjection = true };
        AuditProjectionBacklogState state = new();
        AuditProjectionSnapshot? observed = null;
        AuditProjectionCycle cycle = new(state, clock);

        await Should.ThrowAsync<InvalidOperationException>(() => cycle.ExecuteAsync(
            store, 10, snapshot => observed = snapshot, CancellationToken.None));

        observed.ShouldNotBeNull();
        observed.Count.ShouldBe(1);
        observed.OldestOccurredAt.ShouldBe(oldest);
        AuditProjectionSnapshot failed = state.Read();
        failed.Count.ShouldBe(1);
        failed.OldestOccurredAt.ShouldBe(oldest);
        failed.FailureType.ShouldBe(typeof(InvalidOperationException).FullName);
        AuditProjectionHealthCheck health = new(
            state,
            Options.Create(new AuditProjectionOptions { BacklogWarningAge = TimeSpan.FromMinutes(2) }),
            clock);
        (await health.CheckHealthAsync(new HealthCheckContext())).Status.ShouldBe(HealthStatus.Unhealthy);
    }

    [TestMethod]
    public async Task InitialBacklogReadFailureMakesReadinessUnhealthy()
    {
        FixedTimeProvider clock = new(new DateTimeOffset(2026, 9, 11, 10, 3, 0, TimeSpan.Zero));
        MemoryProjectionStore store = new([CreateIntent()]) { FailNextBacklogRead = true };
        AuditProjectionBacklogState state = new();
        AuditProjectionCycle cycle = new(state, clock);

        await Should.ThrowAsync<InvalidOperationException>(() => cycle.ExecuteAsync(
            store, 10, _ => { }, CancellationToken.None));

        state.Read().FailureType.ShouldBe(typeof(InvalidOperationException).FullName);
        AuditProjectionHealthCheck health = new(
            state, Options.Create(new AuditProjectionOptions()), clock);
        (await health.CheckHealthAsync(new HealthCheckContext())).Status.ShouldBe(HealthStatus.Unhealthy);
    }

    private static AuditIntent CreateIntent(DateTimeOffset? occurredAt = null) => AuditIntent.Create(
        Guid.Parse("01992162-8d8d-7d8b-9efe-b50959dc58c0"),
        Guid.Parse("01992162-8d8d-7d8b-9efe-b50959dc58c1"),
        Guid.Parse("01992162-8d8d-7d8b-9efe-b50959dc58c2"),
        AuditActions.MembershipUpdated,
        new AuditTarget("Membership", "member-1", "Member 1"),
        new Dictionary<string, string?> { ["status"] = "Active" },
        occurredAt ?? new DateTimeOffset(2026, 9, 11, 10, 20, 0, TimeSpan.Zero));

    private sealed class MemoryProjectionStore(IReadOnlyList<AuditIntent> intents) : IAuditIntentProjectionStore
    {
        private readonly HashSet<Guid> projected = [];

        public Guid IntentId => intents.Single().Id;
        public bool FailNextProjection { get; set; }
        public bool FailNextBacklogRead { get; set; }
        public Guid[] ProjectedIds => projected.Order().ToArray();

        public Task<AuditProjectionBacklog> ReadBacklogAsync(CancellationToken cancellationToken)
        {
            if (FailNextBacklogRead)
            {
                FailNextBacklogRead = false;
                throw new InvalidOperationException("injected backlog read failure");
            }
            AuditIntent[] pending = intents.Where(intent => !projected.Contains(intent.Id)).ToArray();
            return Task.FromResult(new AuditProjectionBacklog(
                pending.LongLength,
                pending.Length == 0 ? null : pending.Min(intent => intent.OccurredAt)));
        }

        public Task<IReadOnlyList<AuditIntent>> ReadPendingAsync(int batchSize, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AuditIntent>>(intents.Where(intent => !projected.Contains(intent.Id)).Take(batchSize).ToArray());

        public Task<bool> ProjectAsync(AuditIntent intent, CancellationToken cancellationToken)
        {
            if (FailNextProjection)
            {
                FailNextProjection = false;
                throw new InvalidOperationException("injected projection failure");
            }
            return Task.FromResult(projected.Add(intent.Id));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
