using Shouldly;
using Trykatch.Application.Auditing;
using Trykatch.Application.Authorization;
using Trykatch.Application.Common;
using Trykatch.Application.Organizations;
using Trykatch.Application.Outbox;
using Trykatch.Domain.Common;
using Trykatch.Modules;
using Trykatch.Modules.Projects.Application;
using Trykatch.Modules.Projects.Domain;

namespace Trykatch.Modules.Projects.UnitTests;

[TestClass]
public sealed class ProjectFeatureUseCasesTests
{
    private static readonly Guid OrganizationId = Guid.Parse("70bd4d87-1c40-4eb0-93bd-c06bab0d4481");
    private static readonly Guid ActorId = Guid.Parse("8679f1ba-1a5e-43c7-85fc-52e9f7ac6270");

    [TestMethod]
    public async Task ReadsAndWritesAreIndependentAndNeverGrantPermission()
    {
        TestStore store = new();
        TestCache cache = new();
        TestFeatures features = new() { ReadsEnabled = false, WritesEnabled = true };
        ProjectUseCases useCases = Create(store, cache, features, permission: true);

        (await useCases.ListAsync(1, 10, null, RecordLifecycleFilter.Active, CancellationToken.None)).ErrorCode.ShouldBe("feature_unavailable");
        (await useCases.GetAsync(Guid.NewGuid(), CancellationToken.None)).ErrorCode.ShouldBe("feature_unavailable");
        store.Reads.ShouldBe(0);
        cache.Gets.ShouldBe(0);
        (await useCases.CreateAsync(new("Launch", null), CancellationToken.None)).IsSuccess.ShouldBeTrue();
        store.Writes.ShouldBeGreaterThan(0);

        TestStore deniedStore = new();
        TestCache deniedCache = new();
        TestFeatures enabled = new();
        ProjectUseCases denied = Create(deniedStore, deniedCache, enabled, permission: false);
        (await denied.CreateAsync(new("Launch", null), CancellationToken.None)).ErrorCode.ShouldBe("forbidden");
        (await denied.ListAsync(1, 10, null, RecordLifecycleFilter.Active, CancellationToken.None)).ErrorCode.ShouldBe("forbidden");
        enabled.Calls.ShouldBeEmpty();
        deniedStore.Reads.ShouldBe(0);
        deniedStore.Writes.ShouldBe(0);
    }

    [TestMethod]
    public async Task DisabledWritesRejectEveryMutationBeforeValidationOrProjectStorage()
    {
        TestStore store = new();
        TestCache cache = new();
        TestFeatures features = new() { WritesEnabled = false };
        ProjectUseCases useCases = Create(store, cache, features, permission: true);
        Guid id = Guid.NewGuid();

        (await useCases.CreateAsync(new(" ", null), CancellationToken.None)).ErrorCode.ShouldBe("feature_unavailable");
        (await useCases.UpdateAsync(new(id, "Updated", null), CancellationToken.None)).ErrorCode.ShouldBe("feature_unavailable");
        (await useCases.ArchiveAsync(id, CancellationToken.None)).ErrorCode.ShouldBe("feature_unavailable");
        (await useCases.RestoreAsync(id, CancellationToken.None)).ErrorCode.ShouldBe("feature_unavailable");
        (await useCases.DeleteAsync(id, new DeleteRecordCommand("short"), CancellationToken.None)).ErrorCode.ShouldBe("feature_unavailable");

        features.Calls.ShouldAllBe(key => key == ProjectFeatures.Writes);
        store.Reads.ShouldBe(0);
        store.Writes.ShouldBe(0);
        cache.Gets.ShouldBe(0);
        cache.Invalidations.ShouldBe(0);
        store.AuditEntries.ShouldBe(0);
        store.OutboxEntries.ShouldBe(0);
    }

    private static ProjectUseCases Create(TestStore store, TestCache cache, TestFeatures features, bool permission) =>
        new(store, new TestOrganization(), new TestPermission(permission), store, store,
            new CreateProjectValidator(), new UpdateProjectValidator(), cache, features);

    private sealed class TestFeatures : IOrganizationFeatureEvaluator
    {
        public bool ReadsEnabled { get; set; } = true;
        public bool WritesEnabled { get; set; } = true;
        public List<string> Calls { get; } = [];
        public Task<bool> IsEnabledAsync(string featureKey, CancellationToken ct)
        {
            Calls.Add(featureKey);
            return Task.FromResult(featureKey == ProjectFeatures.Reads ? ReadsEnabled : WritesEnabled);
        }
    }

    private sealed class TestOrganization : IOrganizationContext
    {
        public bool IsResolved => true;
        public Guid OrganizationId => ProjectFeatureUseCasesTests.OrganizationId;
        public string OrganizationSlug => "test";
        public Guid ActorId => ProjectFeatureUseCasesTests.ActorId;
        public Guid MembershipId => Guid.NewGuid();
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>(StringComparer.Ordinal);
    }

    private sealed class TestPermission(bool granted) : IPermissionAuthorizer
    {
        public Task<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken = default) => Task.FromResult(granted);
    }

    private sealed class TestStore : IProjectStore, IAuditWriter, IOutboxWriter
    {
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public int AuditEntries { get; private set; }
        public int OutboxEntries { get; private set; }
        public Task<PagedResult<Project>> ListAsync(Guid organizationId, int page, int pageSize, string? search,
            RecordLifecycleFilter lifecycle, CancellationToken cancellationToken)
        { Reads++; return Task.FromResult(new PagedResult<Project>([], page, pageSize, 0)); }
        public Task<Project?> FindAsync(Guid organizationId, Guid projectId, CancellationToken cancellationToken)
        { Reads++; return Task.FromResult<Project?>(null); }
        public Task AddAsync(Project project, CancellationToken cancellationToken) { Writes++; return Task.CompletedTask; }
        public Task SaveChangesAsync(CancellationToken cancellationToken) { Writes++; return Task.CompletedTask; }
        public void Record(string action, AuditTarget target, IReadOnlyDictionary<string, string?>? details = null) => AuditEntries++;
        public void Enqueue<T>(T message) where T : notnull => OutboxEntries++;
        public void Enqueue<T>(T message, string operationKey, int operationVersion) where T : notnull => OutboxEntries++;
    }

    private sealed class TestCache : IOrganizationCache
    {
        public int Gets { get; private set; }
        public int Invalidations { get; private set; }
        public Task<T> GetOrCreateAsync<T>(ModuleCacheKey key, Func<CancellationToken, Task<T>> factory,
            CancellationToken cancellationToken = default)
        { Gets++; return factory(cancellationToken); }
        public Task InvalidateAsync(ModuleCacheKey key, CancellationToken cancellationToken = default)
        { Invalidations++; return Task.CompletedTask; }
    }
}
