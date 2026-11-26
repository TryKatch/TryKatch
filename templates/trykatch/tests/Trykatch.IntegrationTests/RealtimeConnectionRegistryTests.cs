using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.Extensions.Options;
using Shouldly;
using Trykatch.Api.Realtime;
using Trykatch.Application.Identity;
using Trykatch.Modules;

namespace Trykatch.IntegrationTests;

[TestClass]
public sealed class RealtimeConnectionRegistryTests
{
    [TestMethod]
    public void RegistryEnforcesConnectionLimitAndDuplicateIdsDoNotReplaceEntries()
    {
        RealtimeConnectionRegistry registry = CreateRegistry(maximumConnections: 1);
        RealtimeConnectionBinding binding = CreateBinding();
        FakeConnectionLifetimeFeature firstLifetime = new();

        registry.TryAdd("connection", binding, firstLifetime, 0, out RealtimeConnectionEntry? first).ShouldBeTrue();
        first.ShouldNotBeNull();

        registry.TryAdd("connection", CreateBinding(), new FakeConnectionLifetimeFeature(), 10, out RealtimeConnectionEntry? duplicate)
            .ShouldBeFalse();
        duplicate.ShouldBeNull();
        registry.TryAdd("another", binding, new FakeConnectionLifetimeFeature(), 0, out RealtimeConnectionEntry? overLimit)
            .ShouldBeFalse();
        overLimit.ShouldBeNull();

        registry.Count.ShouldBe(1);
        registry.TryGet("connection", out RealtimeConnectionEntry? current).ShouldBeTrue();
        ReferenceEquals(first, current).ShouldBeTrue();
        first!.Binding.ShouldBe(binding);
    }

    [TestMethod]
    public void SnapshotIsBoundedAndReturnsAnIndependentArray()
    {
        RealtimeConnectionRegistry registry = CreateRegistry(maximumConnections: 2);
        registry.TryAdd("one", CreateBinding(), new FakeConnectionLifetimeFeature(), 0, out RealtimeConnectionEntry? one).ShouldBeTrue();
        registry.TryAdd("two", CreateBinding(), new FakeConnectionLifetimeFeature(), 0, out RealtimeConnectionEntry? two).ShouldBeTrue();

        RealtimeConnectionEntry[] snapshot = registry.Snapshot();
        snapshot.Length.ShouldBe(2);
        snapshot[0] = null!;
        registry.Remove(one!, abort: false);

        registry.Count.ShouldBe(1);
        registry.Snapshot().ShouldBe(new[] { two });
    }

    [TestMethod]
    public void SubscriptionReplacementCopiesInputAndRejectsOversizedUpdateWithoutMutation()
    {
        RealtimeConnectionRegistry registry = CreateRegistry(maximumSubscriptions: 1);
        registry.TryAdd("connection", CreateBinding(), new FakeConnectionLifetimeFeature(), 0, out RealtimeConnectionEntry? entry)
            .ShouldBeTrue();
        ModuleRealtimeAudience original = CreateAudience("original");
        ModuleRealtimeAudience callerMutation = CreateAudience("caller-mutation");
        ModuleRealtimeAudience[] supplied = [original];

        entry!.TryReplace(supplied).ShouldBeTrue();
        (long version, IReadOnlyList<ModuleRealtimeAudience> items) = entry.CaptureSubscriptions();
        supplied[0] = callerMutation;
        items.ShouldBe(new[] { original });

        entry.TryReplace([CreateAudience("too-many"), CreateAudience("too-many-again")]).ShouldBeFalse();
        (long afterRejectedVersion, IReadOnlyList<ModuleRealtimeAudience> afterRejectedItems) = entry.CaptureSubscriptions();
        afterRejectedVersion.ShouldBe(version);
        afterRejectedItems.ShouldBe(new[] { original });

        entry.TryReplace([callerMutation]).ShouldBeTrue();
        entry.IsCurrent(version).ShouldBeFalse();
        entry.CaptureSubscriptions().Items.ShouldBe(new[] { callerMutation });
        entry.Close(abort: false);
        entry.IsCurrent(entry.CaptureSubscriptions().Version).ShouldBeFalse();
    }

    [TestMethod]
    public void SenderGateAllowsOnlyOneActiveSendAndClosedEntryCannotBegin()
    {
        RealtimeConnectionRegistry registry = CreateRegistry();
        registry.TryAdd("connection", CreateBinding(), new FakeConnectionLifetimeFeature(), 0, out RealtimeConnectionEntry? entry)
            .ShouldBeTrue();

        entry!.TryBegin().ShouldBeTrue();
        entry.TryBegin().ShouldBeFalse();
        entry.End();
        entry.TryBegin().ShouldBeTrue();
        entry.End();

        entry.Close(abort: false);
        entry.TryBegin().ShouldBeFalse();
    }

    [TestMethod]
    public void PositionOnlyAdvancesAndRejectsNegativeValues()
    {
        RealtimeConnectionRegistry registry = CreateRegistry();
        registry.TryAdd("connection", CreateBinding(), new FakeConnectionLifetimeFeature(), 5, out RealtimeConnectionEntry? entry)
            .ShouldBeTrue();

        entry!.Position.ShouldBe(5);
        entry.Advance(4);
        entry.Position.ShouldBe(5);
        entry.Advance(8);
        entry.Position.ShouldBe(8);
        Action advanceNegative = () => entry.Advance(-1);
        Should.Throw<ArgumentOutOfRangeException>(advanceNegative);
        entry.Position.ShouldBe(8);

        registry.TryAdd("other", CreateBinding(), new FakeConnectionLifetimeFeature(), 2, out RealtimeConnectionEntry? other)
            .ShouldBeTrue();
        entry.Advance(9);
        other!.Position.ShouldBe(2);
    }

    [TestMethod]
    public void RemovalHidesEntryBeforeSynchronousAbortAndIsIdempotent()
    {
        RealtimeConnectionRegistry registry = CreateRegistry();
        FakeConnectionLifetimeFeature lifetime = new();
        registry.TryAdd("connection", CreateBinding(), lifetime, 0, out RealtimeConnectionEntry? entry).ShouldBeTrue();
        bool wasAbsentDuringAbort = false;
        lifetime.OnAbort = () => wasAbsentDuringAbort = !registry.TryGet("connection", out _);

        registry.Remove(entry!, abort: true);
        wasAbsentDuringAbort.ShouldBeTrue();
        lifetime.AbortCount.ShouldBe(1);
        registry.Count.ShouldBe(0);

        registry.Remove(entry!, abort: true);
        lifetime.AbortCount.ShouldBe(1);
    }

    [TestMethod]
    public void NormalRemovalDoesNotAbortTransport()
    {
        RealtimeConnectionRegistry registry = CreateRegistry();
        FakeConnectionLifetimeFeature lifetime = new();
        registry.TryAdd("connection", CreateBinding(), lifetime, 0, out RealtimeConnectionEntry? entry).ShouldBeTrue();

        registry.Remove(entry!, abort: false);

        lifetime.AbortCount.ShouldBe(0);
        registry.TryGet("connection", out _).ShouldBeFalse();
    }

    [TestMethod]
    public void RegistryDoesNotKeepLifetimeFeatureOrItsRequestGraphAlive()
    {
        (RealtimeConnectionRegistry registry, RealtimeConnectionEntry entry, WeakReference lifetime, WeakReference requestGraph)
            = CreateWeakReferences();

        ForceCollection();

        lifetime.IsAlive.ShouldBeFalse();
        requestGraph.IsAlive.ShouldBeFalse();
        registry.TryGet(entry.ConnectionId, out RealtimeConnectionEntry? registered).ShouldBeTrue();
        ReferenceEquals(entry, registered).ShouldBeTrue();
        entry.HasTransport.ShouldBeFalse();
    }

    private static RealtimeConnectionRegistry CreateRegistry(int maximumConnections = 4, int maximumSubscriptions = 4) =>
        new(Options.Create(new RealtimeOptions
        {
            MaximumConnections = maximumConnections,
            MaximumSubscriptions = maximumSubscriptions
        }));

    private static RealtimeConnectionBinding CreateBinding() => new(
        new AccountSessionProof(Guid.NewGuid(), "session", "stamp", DateTimeOffset.UtcNow.AddMinutes(5)),
        Guid.NewGuid(),
        Guid.NewGuid());

    private static ModuleRealtimeAudience CreateAudience(string key) => new(
        new ModuleRealtimePolicyRef("test-module", key, 1),
        ModuleRealtimeAudienceKind.Organization);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (RealtimeConnectionRegistry Registry, RealtimeConnectionEntry Entry, WeakReference Lifetime, WeakReference RequestGraph)
        CreateWeakReferences()
    {
        RealtimeConnectionRegistry registry = CreateRegistry();
        object requestGraph = new();
        FakeConnectionLifetimeFeature lifetime = new(requestGraph);
        registry.TryAdd("weak-connection", CreateBinding(), lifetime, 0, out RealtimeConnectionEntry? entry).ShouldBeTrue();
        return (registry, entry!, new WeakReference(lifetime), new WeakReference(requestGraph));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private sealed class FakeConnectionLifetimeFeature(object? requestGraph = null) : IConnectionLifetimeFeature
    {
        private readonly object? _requestGraph = requestGraph;

        public CancellationToken ConnectionClosed { get; set; }
        public int AbortCount { get; private set; }
        public Action? OnAbort { get; set; }
        public object? RequestGraph => _requestGraph;

        public void Abort()
        {
            AbortCount++;
            OnAbort?.Invoke();
        }
    }
}
