using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Trykatch.Api.Realtime;
using Trykatch.Application.Identity;
using Trykatch.Application.Organizations;
using Trykatch.Application.Realtime;
using Trykatch.Domain.Organizations;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Modules;

namespace Trykatch.IntegrationTests;

[TestClass]
public sealed class RealtimeDeliveryTests
{
    [TestMethod]
    public async Task RunOnceSendsOnlyRowsAfterPositionAndAdvancesThroughScannedPageOnce()
    {
        using DeliveryFixture fixture = new();
        ModuleRealtimeAudience audience = Audience("policy", ModuleRealtimeAudienceKind.Organization);
        (RealtimeConnectionEntry entry, _) = fixture.AddConnection("connection", 5, audience);
        RealtimeHistoryEntry first = History(6, audience);
        RealtimeHistoryEntry second = History(8, audience);
        RealtimeHistoryEntry filtered = History(7, Audience("another-policy", ModuleRealtimeAudienceKind.Organization));
        fixture.History.Page = after => after < 7
            ? new([first, filtered], 7, true)
            : after < 9 ? new([second], 9, false) : new([], after, false);

        await fixture.Operations.RunOnceAsync(CancellationToken.None);
        await fixture.Operations.RunOnceAsync(CancellationToken.None);
        await fixture.Operations.RunOnceAsync(CancellationToken.None);

        fixture.Transport.Sends.Count.ShouldBe(2);
        fixture.Transport.Sends.Select(send => send.Payload).ShouldBe(new[] { first.Notification, second.Notification });
        entry.Position.ShouldBe(9);
        fixture.History.AfterValues.ShouldBe(new long[] { 5, 7, 9 });
    }

    [TestMethod]
    public async Task RunOnceRequiresExactPolicyKindUserAndResourceSubscriptionMatchAndSendsNotificationOnly()
    {
        using DeliveryFixture fixture = new();
        Guid userId = Guid.NewGuid();
        ModuleRealtimeAudience registered = Audience("policy", ModuleRealtimeAudienceKind.User, userId,
            new ModuleRealtimeResourceRef("document", Guid.NewGuid()));
        fixture.AddConnection("connection", 0, registered);
        RealtimeHistoryEntry[] candidates =
        [
            History(1, Audience("other-policy", ModuleRealtimeAudienceKind.User, userId, registered.Resource)),
            History(2, Audience("policy", ModuleRealtimeAudienceKind.Organization, userId, registered.Resource)),
            History(3, Audience("policy", ModuleRealtimeAudienceKind.User, Guid.NewGuid(), registered.Resource)),
            History(4, Audience("policy", ModuleRealtimeAudienceKind.User, userId, new("document", Guid.NewGuid()))),
            History(5, registered)
        ];
        fixture.History.Page = after => new(candidates.Where(row => row.Position > after).ToArray(), 5, false);

        await fixture.Operations.RunOnceAsync(CancellationToken.None);

        fixture.Transport.Sends.Count.ShouldBe(1);
        fixture.Transport.Sends[0].Method.ShouldBe("invalidate");
        fixture.Transport.Sends[0].Payload.ShouldBe(candidates[^1].Notification);
        fixture.Transport.Sends[0].ArgumentsCount.ShouldBe(1);
    }

    [TestMethod]
    public async Task EachNotificationGetsFreshAuthorityAndItsTransactionIsDisposedBeforeTransportSend()
    {
        using DeliveryFixture fixture = new();
        ModuleRealtimeAudience audience = Audience("policy", ModuleRealtimeAudienceKind.Organization);
        fixture.AddConnection("connection", 0, audience);
        RealtimeHistoryEntry[] rows = [History(1, audience), History(2, audience)];
        fixture.History.Page = after => new(rows.Where(row => row.Position > after).ToArray(), 2, false);
        int policyCalls = 0;
        fixture.Authorization.NotificationHandler = (_, _) => Task.FromResult(++policyCalls == 1);
        fixture.Transport.OnSend = (_, _) =>
        {
            fixture.Scopes.Active.ShouldBe(0);
            return Task.CompletedTask;
        };

        await fixture.Operations.RunOnceAsync(CancellationToken.None);

        fixture.Transport.Sends.Count.ShouldBe(1);
        fixture.Transport.Sends[0].Payload.ShouldBe(rows[0].Notification);
        policyCalls.ShouldBe(2);
        fixture.Scopes.BeginCount.ShouldBe(3);
        fixture.Scopes.Active.ShouldBe(0);
    }

    [TestMethod]
    public async Task SendTimeoutAbortsAndRemovesConnectionWithoutAdvancingPastUnsentRow()
    {
        using DeliveryFixture fixture = new(sendTimeout: TimeSpan.FromMilliseconds(100));
        ModuleRealtimeAudience audience = Audience("policy", ModuleRealtimeAudienceKind.Organization);
        (RealtimeConnectionEntry entry, FakeConnectionLifetimeFeature lifetime) = fixture.AddConnection("connection", 4, audience);
        RealtimeHistoryEntry sent = History(5, audience);
        RealtimeHistoryEntry unsent = History(6, audience);
        fixture.History.Page = after => new(new[] { sent, unsent }.Where(row => row.Position > after).ToArray(), 6, false);
        int sendAttempt = 0;
        fixture.Transport.OnSend = async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref sendAttempt) == 2)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        };

        await fixture.Operations.RunOnceAsync(CancellationToken.None);

        fixture.Registry.TryGet("connection", out _).ShouldBeFalse();
        lifetime.AbortCount.ShouldBe(1);
        entry.Position.ShouldBe(5);
        fixture.Transport.Sends.Count.ShouldBe(2);
    }

    [TestMethod]
    public async Task ConcurrentRunsKeepOneSenderPerConnectionAndRespectGlobalSendBound()
    {
        using DeliveryFixture fixture = new(maximumConcurrentSends: 2);
        ModuleRealtimeAudience audience = Audience("policy", ModuleRealtimeAudienceKind.Organization);
        RealtimeHistoryEntry row = History(1, audience);
        fixture.History.Page = after => after < 1 ? new([row], 1, false) : new([], after, false);
        for (int index = 0; index < 3; index++) fixture.AddConnection($"connection-{index}", 0, audience);

        TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> twoSendsStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int inFlight = 0;
        int maximumObserved = 0;
        fixture.Transport.OnSend = async (_, _) =>
        {
            int current = Interlocked.Increment(ref inFlight);
            UpdateMaximum(ref maximumObserved, current);
            if (current == 2) twoSendsStarted.TrySetResult(true);
            try { await release.Task; }
            finally { Interlocked.Decrement(ref inFlight); }
        };

        Task firstRun = fixture.Operations.RunOnceAsync(CancellationToken.None);
        await twoSendsStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task overlappingRun = fixture.Operations.RunOnceAsync(CancellationToken.None);
        await Task.Delay(30);
        fixture.Transport.Sends.Count.ShouldBe(2);
        release.TrySetResult(true);
        await Task.WhenAll(firstRun, overlappingRun);

        maximumObserved.ShouldBe(2);
        fixture.Transport.Sends.Count.ShouldBe(3);
        fixture.Transport.Sends.Select(send => send.ConnectionId).Distinct().Count().ShouldBe(3);
    }

    [TestMethod]
    public async Task CancellationDoesNotDisposeScopeUntilPolicyCallbackThatIgnoresTokenReturns()
    {
        using DeliveryFixture fixture = new();
        ModuleRealtimeAudience audience = Audience("policy", ModuleRealtimeAudienceKind.Organization);
        fixture.AddConnection("connection", 0, audience);
        RealtimeHistoryEntry row = History(1, audience);
        fixture.History.Page = _ => new([row], 1, false);
        TaskCompletionSource<bool> policyStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releasePolicy = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Authorization.NotificationHandler = async (_, _) =>
        {
            policyStarted.TrySetResult(true);
            await releasePolicy.Task;
            return false;
        };
        using CancellationTokenSource cancellation = new();

        Task run = fixture.Operations.RunOnceAsync(cancellation.Token);
        await policyStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Task.Delay(30);
        run.IsCompleted.ShouldBeFalse();
        fixture.Scopes.Active.ShouldBe(1);

        releasePolicy.TrySetResult(true);
        await Should.ThrowAsync<OperationCanceledException>(() => run);

        fixture.Scopes.Active.ShouldBe(0);
        fixture.Transport.Sends.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task PolicyFailureQuarantinesConnectionWithoutSendingPayload()
    {
        using DeliveryFixture fixture = new();
        ModuleRealtimeAudience audience = Audience("policy", ModuleRealtimeAudienceKind.Organization);
        fixture.AddConnection("connection", 0, audience);
        RealtimeHistoryEntry row = History(1, audience);
        fixture.History.Page = _ => new([row], 1, false);
        fixture.Authorization.NotificationHandler = (_, _) => throw new InvalidOperationException("policy failure");

        await fixture.Operations.RunOnceAsync(CancellationToken.None);

        fixture.Transport.Sends.ShouldBeEmpty();
        fixture.Registry.Count.ShouldBe(0);
        fixture.Scopes.Active.ShouldBe(0);
    }

    private static ModuleRealtimeAudience Audience(string policy, ModuleRealtimeAudienceKind kind, Guid? userId = null,
        ModuleRealtimeResourceRef? resource = null) => new(new("module", policy, 1), kind, userId, resource);

    private static RealtimeHistoryEntry History(long position, ModuleRealtimeAudience audience) => new(position,
        new("module", "changed", 1, Guid.NewGuid(), audience));

    private static void UpdateMaximum(ref int maximum, int value)
    {
        int observed;
        while (value > (observed = Volatile.Read(ref maximum))
            && Interlocked.CompareExchange(ref maximum, value, observed) != observed) { }
    }

    private sealed class DeliveryFixture : IDisposable
    {
        private readonly ServiceProvider provider;
        private readonly FakeHubContext hubContext;
        private readonly List<FakeConnectionLifetimeFeature> lifetimes = [];
        private readonly AccountSessionProof proof = new(Guid.NewGuid(), "session", "stamp", DateTimeOffset.UtcNow.AddMinutes(5));
        private readonly Guid organizationId = Guid.NewGuid();
        private readonly Guid generation = Guid.NewGuid();

        internal DeliveryFixture(int maximumConcurrentSends = 4, TimeSpan? sendTimeout = null)
        {
            Options = Microsoft.Extensions.Options.Options.Create(new RealtimeOptions
            {
                MaximumConnections = 8,
                MaximumSubscriptions = 8,
                MaximumConcurrentSends = maximumConcurrentSends,
                AuthorityTimeout = TimeSpan.FromSeconds(5),
                SendTimeout = sendTimeout ?? TimeSpan.FromSeconds(1)
            });
            Registry = new RealtimeConnectionRegistry(Options);
            Scopes = new TrackingDataScopeFactory();
            Sessions = new FakeAccountSessions(new(organizationId, generation, DateTimeOffset.UtcNow.AddMinutes(5)));
            History = new FakeHistoryReader();
            Authorization = new FakeAuthorization();
            Transport = new RecordingTransport();
            hubContext = new FakeHubContext(Transport);

            ServiceCollection services = new();
            services.AddSingleton(Options);
            services.AddSingleton(Registry);
            services.AddSingleton<IHubContext<ModuleRealtimeHub>>(hubContext);
            services.AddSingleton<RealtimeConnectionOperations>();
            services.AddScoped<IAccountSessions>(_ => Sessions);
            services.AddScoped<IOrganizationDataScopeFactory>(_ => Scopes);
            services.AddScoped<IOrganizationAccessResolver>(_ => new FakeOrganizationAccessResolver(organizationId));
            services.AddScoped<IOrganizationDataPlacement>(_ => new FakeOrganizationDataPlacement(organizationId));
            services.AddScoped<IOrganizationContextInitializer, FakeOrganizationContextInitializer>();
            services.AddScoped<RealtimeAuthority>();
            services.AddScoped<IRealtimeHistoryReader>(_ => History);
            services.AddScoped<IRealtimeAudienceAuthorization>(_ => Authorization);
            provider = services.BuildServiceProvider();
            Operations = provider.GetRequiredService<RealtimeConnectionOperations>();
        }

        internal IOptions<RealtimeOptions> Options { get; }
        internal RealtimeConnectionRegistry Registry { get; }
        internal TrackingDataScopeFactory Scopes { get; }
        internal FakeAccountSessions Sessions { get; }
        internal FakeHistoryReader History { get; }
        internal FakeAuthorization Authorization { get; }
        internal RecordingTransport Transport { get; }
        internal RealtimeConnectionOperations Operations { get; }

        internal (RealtimeConnectionEntry Entry, FakeConnectionLifetimeFeature Lifetime) AddConnection(
            string id, long position, params ModuleRealtimeAudience[] subscriptions)
        {
            FakeConnectionLifetimeFeature lifetime = new();
            Registry.TryAdd(id, new(proof, organizationId, generation), lifetime, position, out RealtimeConnectionEntry? entry)
                .ShouldBeTrue();
            lifetimes.Add(lifetime);
            entry!.TryReplace(subscriptions).ShouldBeTrue();
            return (entry, lifetime);
        }

        public void Dispose()
        {
            provider.Dispose();
        }
    }

    private sealed class FakeHistoryReader : IRealtimeHistoryReader
    {
        internal Func<long, RealtimeHistoryBatch> Page { get; set; } = after => new([], after, false);
        internal ConcurrentQueue<long> AfterValues { get; } = new();
        public Task<long> ReadHeadAsync(CancellationToken cancellationToken = default) => Task.FromResult(0L);
        public Task<RealtimeHistoryBatch> ReadPageAsync(long after, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AfterValues.Enqueue(after);
            return Task.FromResult(Page(after));
        }
    }

    private sealed class FakeAuthorization : IRealtimeAudienceAuthorization
    {
        internal Func<ModuleRealtimeAudience, CancellationToken, Task<bool>> AudienceHandler { get; set; }
            = (_, _) => Task.FromResult(true);
        internal Func<ModuleRealtimeNotification, CancellationToken, Task<bool>> NotificationHandler { get; set; }
            = (_, _) => Task.FromResult(true);
        public Task<bool> AuthorizeAsync(ModuleRealtimeAudience audience, CancellationToken cancellationToken = default) =>
            AudienceHandler(audience, cancellationToken);
        public Task<bool> AuthorizeAsync(ModuleRealtimeNotification notification, CancellationToken cancellationToken = default) =>
            NotificationHandler(notification, cancellationToken);
    }

    private sealed class FakeAccountSessions(AccountSessionState? initialState) : IAccountSessions
    {
        internal AccountSessionState? State { get; set; } = initialState;
        internal int ValidateCount { get; private set; }
        public Task<AccountSessionState?> ValidateAsync(AccountSessionProof proof, CancellationToken cancellationToken = default)
        {
            ValidateCount++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(State);
        }
        public Task<AccountSessionState?> BindWorkspaceAsync(AccountSessionProof proof, Guid? organizationId,
            CancellationToken cancellationToken = default) => Task.FromResult(State);
    }

    private sealed class TrackingDataScopeFactory : IOrganizationDataScopeFactory
    {
        private int active;
        private int beginCount;
        internal int Active => Volatile.Read(ref active);
        internal int BeginCount => Volatile.Read(ref beginCount);
        public Task<IOrganizationDataScope> BeginAsync(Guid organizationId, Guid actorId, CancellationToken cancellationToken) =>
            BeginReadOnlyAsync(organizationId, actorId, cancellationToken);
        public Task<IOrganizationDataScope> BeginReadOnlyAsync(Guid organizationId, Guid actorId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref active);
            Interlocked.Increment(ref beginCount);
            return Task.FromResult<IOrganizationDataScope>(new TrackingDataScope(this));
        }
        private sealed class TrackingDataScope(TrackingDataScopeFactory owner) : IOrganizationDataScope
        {
            private bool disposed;
            public Task CommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public ValueTask DisposeAsync()
            {
                if (!disposed) { disposed = true; Interlocked.Decrement(ref owner.active); }
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakeOrganizationAccessResolver(Guid organizationId) : IOrganizationAccessResolver
    {
        public Task<OrganizationAccess?> ResolveAsync(Guid actorId, Guid requestedOrganizationId,
            CancellationToken cancellationToken = default) => Task.FromResult<OrganizationAccess?>(new(
                organizationId, "test-org", actorId, Guid.NewGuid(), new HashSet<string>(StringComparer.Ordinal)));
    }

    private sealed class FakeOrganizationDataPlacement(Guid organizationId) : IOrganizationDataPlacement
    {
        public Task<OrganizationDataPlacementResult> ProvisionAsync(OrganizationDataPlacementRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<OrganizationDataRoute> ResolveAsync(Guid requestedOrganizationId, CancellationToken cancellationToken) =>
            Task.FromResult(new OrganizationDataRoute(organizationId, OrganizationDataPlacementKind.Shared,
                "postgres", "test-db", "test-secret", "1"));
    }

    private sealed class FakeOrganizationContextInitializer : IOrganizationContextInitializer
    {
        public void Initialize(OrganizationAccess access) { }
    }

    private sealed class FakeConnectionLifetimeFeature : Microsoft.AspNetCore.Connections.Features.IConnectionLifetimeFeature
    {
        public CancellationToken ConnectionClosed { get; set; }
        internal int AbortCount { get; private set; }
        public void Abort() => AbortCount++;
    }

    private sealed record RecordedSend(string ConnectionId, string Method, object? Payload, int ArgumentsCount);

    private sealed class RecordingTransport
    {
        private readonly object sync = new();
        internal List<RecordedSend> Sends { get; } = [];
        internal Func<string, CancellationToken, Task> OnSend { get; set; } = (_, _) => Task.CompletedTask;
        internal async Task SendAsync(string connectionId, string method, object?[] args, CancellationToken cancellationToken)
        {
            object? payload = args.Length == 0 ? null : args[0];
            lock (sync) Sends.Add(new(connectionId, method, payload, args.Length));
            await OnSend(connectionId, cancellationToken);
        }
    }

    private sealed class FakeHubContext(RecordingTransport transport) : IHubContext<ModuleRealtimeHub>
    {
        public IHubClients Clients { get; } = new FakeHubClients(transport);
        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class FakeHubClients(RecordingTransport transport) : IHubClients
    {
        public ISingleClientProxy Client(string connectionId) => new FakeClient(transport, connectionId);
        IClientProxy IHubClients<IClientProxy>.Client(string connectionId) => Client(connectionId);
        public IClientProxy All => throw new NotSupportedException();
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();
        public IClientProxy Group(string groupName) => throw new NotSupportedException();
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();
        public IClientProxy User(string userId) => throw new NotSupportedException();
        public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
    }

    private sealed class FakeClient(RecordingTransport transport, string connectionId) : ISingleClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) =>
            transport.SendAsync(connectionId, method, args, cancellationToken);
        public Task<T> InvokeCoreAsync<T>(string method, object?[] args, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
