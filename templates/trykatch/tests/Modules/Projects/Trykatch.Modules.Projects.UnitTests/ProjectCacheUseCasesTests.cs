using System.Security.Cryptography;
using System.Text;
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
public sealed class ProjectCacheUseCasesTests
{
    private static readonly Guid OrganizationId = Guid.Parse("70bd4d87-1c40-4eb0-93bd-c06bab0d4481");
    private static readonly Guid ActorId = Guid.Parse("8679f1ba-1a5e-43c7-85fc-52e9f7ac6270");

    [TestMethod]
    public async Task GetChecksPermissionBeforeUsingCacheAndCachesDetachedDetailDto()
    {
        Project project = Project.Create(OrganizationId, "Launch", "Roadmap", ActorId);
        TestProjectStore store = new() { Project = project };
        RecordingCache cache = new();
        ProjectUseCases useCases = CreateUseCases(store, cache, permission: true);

        Result<ProjectDto> result = await useCases.GetAsync(project.Id, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(new ProjectDto(project.Id, "Launch", "Roadmap", project.CreatedAt, project.UpdatedAt,
            new RecordLifecycleDto("Active", null, null, null)));
        store.FindCalls.ShouldBe(1);
        cache.GetKeys.ShouldHaveSingleItem().ShouldBe(DetailKey(project.Id));
        cache.InvalidationKeys.ShouldBeEmpty();

        RecordingCache warmCache = new() { ReturnCachedValue = true, CachedValue = result.Value };
        TestProjectStore deniedStore = new() { Project = project };
        ProjectUseCases deniedUseCases = CreateUseCases(deniedStore, warmCache, permission: false);
        Result<ProjectDto> denied = await deniedUseCases.GetAsync(project.Id, CancellationToken.None);
        denied.IsSuccess.ShouldBeFalse();
        denied.ErrorCode.ShouldBe("forbidden");
        deniedStore.FindCalls.ShouldBe(0);
        warmCache.GetKeys.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task GetPreservesNotFoundAndListDoesNotUseDetailCache()
    {
        RecordingCache cache = new();
        TestProjectStore store = new();
        ProjectUseCases useCases = CreateUseCases(store, cache, permission: true);

        Result<ProjectDto> missing = await useCases.GetAsync(Guid.NewGuid(), CancellationToken.None);
        Result<PagedResult<ProjectDto>> list = await useCases.ListAsync(1, 10, null, RecordLifecycleFilter.Active, CancellationToken.None);

        missing.IsSuccess.ShouldBeFalse();
        missing.ErrorCode.ShouldBe("not_found");
        list.IsSuccess.ShouldBeTrue();
        store.FindCalls.ShouldBe(1);
        store.ListCalls.ShouldBe(1);
        cache.GetKeys.ShouldHaveSingleItem();
    }

    [TestMethod]
    public async Task CreateUpdateArchiveRestoreAndDeleteSaveThenInvalidateSameDetailKeyOnce()
    {
        await AssertMutationInvalidatesAfterSaveAsync(async (useCases, _) =>
            await useCases.CreateAsync(new CreateProjectCommand("Created", null), CancellationToken.None));

        await AssertMutationInvalidatesAfterSaveAsync(async (useCases, projectId) =>
            await useCases.UpdateAsync(new UpdateProjectCommand(projectId, "Updated", null), CancellationToken.None),
            setup: store => store.Project = Project.Create(OrganizationId, "Existing", null, ActorId));

        await AssertMutationInvalidatesAfterSaveAsync(async (useCases, projectId) =>
            await useCases.ArchiveAsync(projectId, CancellationToken.None),
            setup: store => store.Project = Project.Create(OrganizationId, "Active", null, ActorId));

        await AssertMutationInvalidatesAfterSaveAsync(async (useCases, projectId) =>
            await useCases.RestoreAsync(projectId, CancellationToken.None),
            setup: store =>
            {
                Project project = Project.Create(OrganizationId, "Archived", null, ActorId);
                project.Archive(ActorId, DateTimeOffset.UtcNow);
                store.Project = project;
            });

        await AssertMutationInvalidatesAfterSaveAsync(async (useCases, projectId) =>
            await useCases.DeleteAsync(projectId, new DeleteRecordCommand("Requested by owner"), CancellationToken.None),
            setup: store =>
            {
                Project project = Project.Create(OrganizationId, "Archived", null, ActorId);
                project.Archive(ActorId, DateTimeOffset.UtcNow);
                store.Project = project;
            });
    }

    [TestMethod]
    public async Task DeniedInvalidMissingAndIdempotentMutationPathsDoNotInvalidate()
    {
        RecordingCache deniedCache = new();
        ProjectUseCases denied = CreateUseCases(new TestProjectStore(), deniedCache, permission: false);
        (await denied.CreateAsync(new CreateProjectCommand("Created", null), CancellationToken.None)).ErrorCode.ShouldBe("forbidden");
        deniedCache.InvalidationKeys.ShouldBeEmpty();

        RecordingCache invalidCache = new();
        TestProjectStore invalidStore = new() { Project = Project.Create(OrganizationId, "Archived", null, ActorId) };
        invalidStore.Project.Archive(ActorId, DateTimeOffset.UtcNow);
        ProjectUseCases invalid = CreateUseCases(invalidStore, invalidCache, permission: true);
        (await invalid.CreateAsync(new CreateProjectCommand(" ", null), CancellationToken.None)).ErrorCode.ShouldBe("validation");
        (await invalid.DeleteAsync(invalidStore.Project.Id, new DeleteRecordCommand("short"), CancellationToken.None)).ErrorCode.ShouldBe("validation");
        invalidCache.InvalidationKeys.ShouldBeEmpty();

        RecordingCache missingCache = new();
        ProjectUseCases missing = CreateUseCases(new TestProjectStore(), missingCache, permission: true);
        (await missing.UpdateAsync(new UpdateProjectCommand(Guid.NewGuid(), "Updated", null), CancellationToken.None)).ErrorCode.ShouldBe("not_found");
        (await missing.ArchiveAsync(Guid.NewGuid(), CancellationToken.None)).ErrorCode.ShouldBe("not_found");
        (await missing.RestoreAsync(Guid.NewGuid(), CancellationToken.None)).ErrorCode.ShouldBe("not_found");
        (await missing.DeleteAsync(Guid.NewGuid(), new DeleteRecordCommand("valid deletion reason"), CancellationToken.None)).ErrorCode.ShouldBe("not_found");
        missingCache.InvalidationKeys.ShouldBeEmpty();

        RecordingCache archivedCache = new();
        TestProjectStore archivedStore = new() { Project = Project.Create(OrganizationId, "Archived", null, ActorId) };
        archivedStore.Project.Archive(ActorId, DateTimeOffset.UtcNow);
        ProjectUseCases alreadyArchived = CreateUseCases(archivedStore, archivedCache, permission: true);
        (await alreadyArchived.ArchiveAsync(archivedStore.Project.Id, CancellationToken.None)).IsSuccess.ShouldBeTrue();
        archivedCache.InvalidationKeys.ShouldBeEmpty();
        archivedStore.SaveCalls.ShouldBe(0);

        RecordingCache activeCache = new();
        TestProjectStore activeStore = new() { Project = Project.Create(OrganizationId, "Active", null, ActorId) };
        ProjectUseCases alreadyActive = CreateUseCases(activeStore, activeCache, permission: true);
        (await alreadyActive.RestoreAsync(activeStore.Project.Id, CancellationToken.None)).IsSuccess.ShouldBeTrue();
        activeCache.InvalidationKeys.ShouldBeEmpty();
        activeStore.SaveCalls.ShouldBe(0);
    }

    private static async Task AssertMutationInvalidatesAfterSaveAsync(
        Func<ProjectUseCases, Guid, Task<object>> mutation,
        Action<TestProjectStore>? setup = null)
    {
        TestProjectStore store = new();
        setup?.Invoke(store);
        RecordingCache cache = new();
        ProjectUseCases useCases = CreateUseCases(store, cache, permission: true);
        Guid projectId = store.Project?.Id ?? Guid.Empty;
        object result = await mutation(useCases, projectId);

        result.ShouldNotBeNull();
        store.SaveCalls.ShouldBe(1);
        store.Events.ShouldBe(["save", "invalidate"]);
        cache.InvalidationKeys.ShouldHaveSingleItem();
        Guid resultingProjectId = store.Added.SingleOrDefault()?.Id ?? projectId;
        cache.InvalidationKeys[0].ShouldBe(DetailKey(resultingProjectId));
    }

    private static ProjectUseCases CreateUseCases(TestProjectStore store, RecordingCache cache, bool permission)
    {
        cache.Observe(store);
        return new(store, new TestOrganizationContext(), new TestPermissionAuthorizer(permission), new TestAuditWriter(),
            new TestOutboxWriter(), new CreateProjectValidator(), new UpdateProjectValidator(), cache);
    }

    private static ModuleCacheKey DetailKey(Guid projectId) => new(
        "projects",
        "projects.detail",
        1,
        1,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(projectId.ToString("N")))).ToLowerInvariant(),
        ModuleCacheAudience.Organization,
        "short");

    private sealed class TestOrganizationContext : IOrganizationContext
    {
        public bool IsResolved => true;
        public Guid OrganizationId => ProjectCacheUseCasesTests.OrganizationId;
        public string OrganizationSlug => "test";
        public Guid ActorId => ProjectCacheUseCasesTests.ActorId;
        public Guid MembershipId => Guid.Parse("9dcbaac3-5a89-4e70-ad7d-8cc1a2af85bc");
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>(StringComparer.Ordinal);
    }

    private sealed class TestPermissionAuthorizer(bool granted) : IPermissionAuthorizer
    {
        public Task<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken = default) => Task.FromResult(granted);
    }

    private sealed class TestProjectStore : IProjectStore
    {
        public Project? Project { get; set; }
        public List<Project> Added { get; } = [];
        public List<string> Events { get; } = [];
        public int FindCalls { get; private set; }
        public int ListCalls { get; private set; }
        public int SaveCalls { get; private set; }

        public Task<PagedResult<Project>> ListAsync(Guid organizationId, int page, int pageSize, string? search,
            RecordLifecycleFilter lifecycle, CancellationToken cancellationToken)
        {
            ListCalls++;
            IReadOnlyList<Project> items = Project is null ? [] : [Project];
            return Task.FromResult(new PagedResult<Project>(items, page, pageSize, items.Count));
        }

        public Task<Project?> FindAsync(Guid organizationId, Guid projectId, CancellationToken cancellationToken)
        {
            FindCalls++;
            return Task.FromResult(Project?.Id == projectId ? Project : null);
        }

        public Task AddAsync(Project project, CancellationToken cancellationToken)
        {
            Added.Add(project);
            Project = project;
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            SaveCalls++;
            Events.Add("save");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCache : IOrganizationCache
    {
        public List<ModuleCacheKey> GetKeys { get; } = [];
        public List<ModuleCacheKey> InvalidationKeys { get; } = [];
        public object? CachedValue { get; init; }
        public bool ReturnCachedValue { get; init; }
        private TestProjectStore? store;

        public void Observe(TestProjectStore projectStore) => store = projectStore;

        public async Task<T> GetOrCreateAsync<T>(ModuleCacheKey key, Func<CancellationToken, Task<T>> factory,
            CancellationToken cancellationToken = default)
        {
            GetKeys.Add(key);
            if (ReturnCachedValue) return (T)CachedValue!;
            return await factory(cancellationToken);
        }

        public Task InvalidateAsync(ModuleCacheKey key, CancellationToken cancellationToken = default)
        {
            InvalidationKeys.Add(key);
            store?.Events.Add("invalidate");
            return Task.CompletedTask;
        }
    }

    private sealed class TestAuditWriter : IAuditWriter
    {
        public void Record(string action, AuditTarget target, IReadOnlyDictionary<string, string?>? details = null) { }
        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestOutboxWriter : IOutboxWriter
    {
        public void Enqueue<T>(T message) where T : notnull { }
        public void Enqueue<T>(T message, string operationKey, int operationVersion) where T : notnull { }
    }
}
