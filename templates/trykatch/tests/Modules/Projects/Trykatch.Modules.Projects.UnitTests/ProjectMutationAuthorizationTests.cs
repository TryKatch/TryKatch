using Shouldly;
using Trykatch.Application.Authorization;
using Trykatch.Application.Common;
using Trykatch.Domain.Common;
using Trykatch.Modules;
using Trykatch.Modules.Projects.Application;
using Trykatch.Modules.Projects.Domain;

namespace Trykatch.Modules.Projects.UnitTests;

[TestClass]
public sealed class ProjectMutationAuthorizationTests
{
    private static readonly Guid OrganizationId = Guid.Parse("70bd4d87-1c40-4eb0-93bd-c06bab0d4481");
    private static readonly Guid OtherOrganizationId = Guid.Parse("3d77627a-edaf-4b06-8c70-417f4e2af0d9");
    private static readonly Guid ActorId = Guid.Parse("8679f1ba-1a5e-43c7-85fc-52e9f7ac6270");
    private static readonly Guid MembershipId = Guid.Parse("9dcbaac3-5a89-4e70-ad7d-8cc1a2af85bc");

    [TestMethod]
    public async Task ManagePermissionIsSufficientForCreateAndExistingProjectRequests()
    {
        TestProjectStore store = new();
        Project project = Project.Create(OrganizationId, "Launch", null, ActorId);
        store.Add(project);
        ProjectMutationAuthorization authorization = new(store);
        MutationAuthority authority = Authority(Permissions.ProjectsManage);

        (await authorization.AuthorizeRequestAsync(authority, null, CancellationToken.None)).ShouldBeTrue();
        (await authorization.AuthorizeRequestAsync(authority, project.Id, CancellationToken.None)).ShouldBeTrue();
        store.FindCalls.ShouldBe(1);
        store.Writes.ShouldBe(0);
    }

    [TestMethod]
    public async Task RequestAuthorizationDeniesMissingPermissionAndInvalidAuthorityOrProject()
    {
        TestProjectStore store = new();
        Project project = Project.Create(OrganizationId, "Launch", null, ActorId);
        store.Add(project);
        ProjectMutationAuthorization authorization = new(store);

        (await authorization.AuthorizeRequestAsync(Authority(Permissions.ProjectsRead), null, CancellationToken.None)).ShouldBeFalse();
        (await authorization.AuthorizeRequestAsync(Authority(Permissions.ProjectsManage, organizationId: Guid.Empty), null, CancellationToken.None)).ShouldBeFalse();
        (await authorization.AuthorizeRequestAsync(Authority(Permissions.ProjectsManage, actorId: Guid.Empty), null, CancellationToken.None)).ShouldBeFalse();
        (await authorization.AuthorizeRequestAsync(Authority(Permissions.ProjectsManage, membershipId: Guid.Empty), null, CancellationToken.None)).ShouldBeFalse();
        (await authorization.AuthorizeRequestAsync(Authority(Permissions.ProjectsManage), Guid.Empty, CancellationToken.None)).ShouldBeFalse();
        (await authorization.AuthorizeRequestAsync(Authority(Permissions.ProjectsManage), Guid.NewGuid(), CancellationToken.None)).ShouldBeFalse();
        (await authorization.AuthorizeRequestAsync(Authority(Permissions.ProjectsManage, organizationId: OtherOrganizationId), project.Id, CancellationToken.None)).ShouldBeFalse();
        store.Writes.ShouldBe(0);
    }

    [TestMethod]
    public async Task ReplayRequiresManagePermissionProjectsReferenceAndOrganizationOwnedResource()
    {
        TestProjectStore store = new();
        Project project = Project.Create(OrganizationId, "Launch", null, ActorId);
        store.Add(project);
        ProjectMutationAuthorization authorization = new(store);
        MutationAuthority authority = Authority(Permissions.ProjectsManage);

        (await authorization.AuthorizeReplayAsync(authority, new MutationResourceReference("projects", project.Id), CancellationToken.None)).ShouldBeTrue();
        (await authorization.AuthorizeReplayAsync(Authority(Permissions.ProjectsRead), new MutationResourceReference("projects", project.Id), CancellationToken.None)).ShouldBeFalse();
        (await authorization.AuthorizeReplayAsync(authority, null, CancellationToken.None)).ShouldBeFalse();
        (await authorization.AuthorizeReplayAsync(authority, new MutationResourceReference("other", project.Id), CancellationToken.None)).ShouldBeFalse();
        (await authorization.AuthorizeReplayAsync(authority, new MutationResourceReference("projects", Guid.Empty), CancellationToken.None)).ShouldBeFalse();
        (await authorization.AuthorizeReplayAsync(authority, new MutationResourceReference("projects", Guid.NewGuid()), CancellationToken.None)).ShouldBeFalse();
        (await authorization.AuthorizeReplayAsync(Authority(Permissions.ProjectsManage, organizationId: OtherOrganizationId), new MutationResourceReference("projects", project.Id), CancellationToken.None)).ShouldBeFalse();
        store.Writes.ShouldBe(0);
    }

    [TestMethod]
    public async Task ArchivedAndDeletionRequestedProjectsRemainEligibleForRequestAndReplay()
    {
        foreach (bool deleted in new[] { false, true })
        {
            TestProjectStore store = new();
            Project project = Project.Create(OrganizationId, deleted ? "Deleted" : "Archived", null, ActorId);
            project.Archive(ActorId, DateTimeOffset.UtcNow);
            if (deleted) project.Delete(ActorId, "Requested by the organization owner", DateTimeOffset.UtcNow);
            store.Add(project);

            ProjectMutationAuthorization authorization = new(store);
            MutationAuthority authority = Authority(Permissions.ProjectsManage);
            (await authorization.AuthorizeRequestAsync(authority, project.Id, CancellationToken.None)).ShouldBeTrue();
            (await authorization.AuthorizeReplayAsync(authority, new MutationResourceReference("projects", project.Id), CancellationToken.None)).ShouldBeTrue();
            store.Writes.ShouldBe(0);
        }
    }

    private static MutationAuthority Authority(string permission, Guid? organizationId = null, Guid? actorId = null, Guid? membershipId = null) =>
        new(organizationId ?? OrganizationId, actorId ?? ActorId, membershipId ?? MembershipId,
            new HashSet<string>([permission], StringComparer.Ordinal));

    private sealed class TestProjectStore : IProjectStore
    {
        private readonly List<Project> projects = [];

        public int FindCalls { get; private set; }
        public int Writes { get; private set; }

        public void Add(Project project) => projects.Add(project);

        public Task<PagedResult<Project>> ListAsync(Guid organizationId, int page, int pageSize, string? search,
            RecordLifecycleFilter lifecycle, CancellationToken cancellationToken) =>
            Task.FromResult(new PagedResult<Project>([], page, pageSize, 0));

        public Task<Project?> FindAsync(Guid organizationId, Guid projectId, CancellationToken cancellationToken)
        {
            FindCalls++;
            return Task.FromResult(projects.SingleOrDefault(project => project.OrganizationId == organizationId && project.Id == projectId));
        }

        public Task AddAsync(Project project, CancellationToken cancellationToken)
        {
            Writes++;
            projects.Add(project);
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            Writes++;
            return Task.CompletedTask;
        }
    }
}
