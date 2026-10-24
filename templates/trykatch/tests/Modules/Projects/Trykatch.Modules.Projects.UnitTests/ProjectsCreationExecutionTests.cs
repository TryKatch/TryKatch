using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using Trykatch.Application.Auditing;
using Trykatch.Application.Authorization;
using Trykatch.Application.Common;
using Trykatch.Application.Organizations;
using Trykatch.Application.Outbox;
using Trykatch.Modules;
using Trykatch.Modules.Projects.Application;
using Trykatch.Modules.Projects.Domain;
using Trykatch.Modules.Projects.Infrastructure;
using Trykatch.Modules.Projects.IntegrationEvents;

namespace Trykatch.Modules.Projects.UnitTests;

[TestClass]
public sealed class ProjectsCreationExecutionTests
{
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);
    private static readonly Guid OrganizationId = Guid.Parse("70bd4d87-1c40-4eb0-93bd-c06bab0d4481");
    private static readonly Guid ActorId = Guid.Parse("8679f1ba-1a5e-43c7-85fc-52e9f7ac6270");
    private static readonly Guid MembershipId = Guid.Parse("9dcbaac3-5a89-4e70-ad7d-8cc1a2af85bc");
    private static readonly string[] ManagePermission = ["projects.manage"];
    private static readonly string[] NoPermissions = [];
    private static readonly int[] InvalidPayload = [1];

    [TestMethod]
    public void ProjectsCreationContractsAreExplicitAndBounded()
    {
        ProjectsModule module = new();

        module.Descriptor.Version.ShouldBe("1.1.0");
        module.Descriptor.Operations.ShouldContain(operation =>
            operation.ModuleId == "projects"
            && operation.OperationKey == "projects.create"
            && operation.OperationVersion == 1
            && operation.ExecutionKind == ModuleExecutionKind.UserCommand
            && operation.PayloadSchemaKey == "projects.create-command"
            && operation.PayloadSchemaVersion == 1
            && operation.RequiredPermissionKeys.SequenceEqual(ManagePermission)
            && operation.ResourcePolicyKey == "projects"
            && operation.SystemGrantKey == null
            && operation.MaximumPayloadBytes == 16_384);
        module.Descriptor.IntegrationEvents.ShouldContain(integrationEvent =>
            integrationEvent.EventKey == "projects.created"
            && integrationEvent.SchemaVersion == 1
            && integrationEvent.OwnerModuleId == "projects"
            && integrationEvent.PayloadType == typeof(ProjectCreated)
            && integrationEvent.MaximumPayloadBytes == 1_024);
        module.Descriptor.SystemGrants.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task RegisteredCreateHandlerCallsUseCaseAndWritesLegacyAndTypedEvents()
    {
        var context = new TestOrganizationContext(OrganizationId, ActorId, MembershipId, new HashSet<string>(ManagePermission, StringComparer.Ordinal));
        var store = new TestProjectStore();
        var outbox = new RecordingOutboxWriter();
        var audit = new RecordingAuditWriter();
        using ServiceProvider services = CreateServices(context, store, outbox, audit, permission: true);
        IModuleOperationHandler handler = services.GetServices<IModuleOperationHandler>().ShouldHaveSingleItem();

        await handler.HandleAsync(
            new(Guid.NewGuid(), OrganizationId, ActorId, ActorId, ModuleExecutionKind.UserCommand, "projects.create", 1),
            JsonSerializer.SerializeToElement(new CreateProjectCommand("Launch", "Roadmap"), PayloadJson),
            CancellationToken.None);

        Project project = store.Added.ShouldHaveSingleItem();
        project.OrganizationId.ShouldBe(OrganizationId);
        project.CreatedBy.ShouldBe(ActorId);
        audit.Actions.ShouldBe([AuditActions.ProjectCreated]);
        outbox.LegacyMessages.ShouldHaveSingleItem().ShouldBeOfType<ProjectChanged>();
        outbox.LegacyMessages[0].ShouldBe(new ProjectChanged(OrganizationId, project.Id, "created", ActorId));
        outbox.TypedMessages.ShouldHaveSingleItem().ShouldBe(new ProjectCreated(OrganizationId, project.Id, ActorId));
        outbox.TypedRegistrations.ShouldBe([("projects.create", 1)]);
    }

    [TestMethod]
    public async Task CreateResourcePolicyRejectsMalformedOrMismatchedCurrentContext()
    {
        var context = new TestOrganizationContext(OrganizationId, ActorId, MembershipId, new HashSet<string>(ManagePermission, StringComparer.Ordinal));
        using ServiceProvider services = CreateServices(context, new TestProjectStore(), new RecordingOutboxWriter(), new RecordingAuditWriter(), permission: true);
        IModuleOperationResourcePolicy policy = services.GetServices<IModuleOperationResourcePolicy>().ShouldHaveSingleItem();
        ModuleOperationContext valid = new(Guid.NewGuid(), OrganizationId, ActorId, ActorId, ModuleExecutionKind.UserCommand, "projects.create", 1);
        JsonElement payload = JsonSerializer.SerializeToElement(new CreateProjectCommand("Launch", null), PayloadJson);

        (await policy.AuthorizeAsync(valid, payload, CancellationToken.None)).ShouldBeTrue();
        (await policy.AuthorizeAsync(valid with { OrganizationId = Guid.NewGuid() }, payload, CancellationToken.None)).ShouldBeFalse();
        (await policy.AuthorizeAsync(valid with { EffectiveActorId = Guid.NewGuid() }, payload, CancellationToken.None)).ShouldBeFalse();
        (await policy.AuthorizeAsync(valid with { OperationKey = "projects.archive" }, payload, CancellationToken.None)).ShouldBeFalse();
        (await policy.AuthorizeAsync(valid with { ExecutionKind = ModuleExecutionKind.ScheduledSystem }, payload, CancellationToken.None)).ShouldBeFalse();
        (await policy.AuthorizeAsync(valid, JsonSerializer.SerializeToElement(new { name = " " }, PayloadJson), CancellationToken.None)).ShouldBeFalse();
        (await policy.AuthorizeAsync(valid, JsonSerializer.SerializeToElement(InvalidPayload, PayloadJson), CancellationToken.None)).ShouldBeFalse();
    }

    [TestMethod]
    public async Task CreateResourcePolicyRequiresCurrentProjectsPermission()
    {
        var context = new TestOrganizationContext(OrganizationId, ActorId, MembershipId, new HashSet<string>(NoPermissions, StringComparer.Ordinal));
        using ServiceProvider services = CreateServices(context, new TestProjectStore(), new RecordingOutboxWriter(), new RecordingAuditWriter(), permission: false);
        IModuleOperationResourcePolicy policy = services.GetServices<IModuleOperationResourcePolicy>().ShouldHaveSingleItem();
        ModuleOperationContext valid = new(Guid.NewGuid(), OrganizationId, ActorId, ActorId, ModuleExecutionKind.UserCommand, "projects.create", 1);
        JsonElement payload = JsonSerializer.SerializeToElement(new CreateProjectCommand("Launch", null), PayloadJson);

        (await policy.AuthorizeAsync(valid, payload, CancellationToken.None)).ShouldBeFalse();
    }

    private static ServiceProvider CreateServices(
        TestOrganizationContext context,
        TestProjectStore store,
        RecordingOutboxWriter outbox,
        RecordingAuditWriter audit,
        bool permission)
    {
        ServiceCollection services = new();
        new ProjectsModule().Register(services, new ConfigurationBuilder().Build());
        services.Replace(ServiceDescriptor.Singleton<IOrganizationContext>(context));
        services.Replace(ServiceDescriptor.Singleton<IProjectStore>(store));
        services.Replace(ServiceDescriptor.Singleton<IOutboxWriter>(outbox));
        services.Replace(ServiceDescriptor.Singleton<IAuditWriter>(audit));
        services.Replace(ServiceDescriptor.Singleton<IPermissionAuthorizer>(new TestPermissionAuthorizer(permission)));
        services.Replace(ServiceDescriptor.Singleton<IOrganizationCache>(new PassThroughOrganizationCache()));
        return services.BuildServiceProvider();
    }

    private sealed class TestOrganizationContext(Guid organizationId, Guid actorId, Guid membershipId, IReadOnlySet<string> permissions) : IOrganizationContext
    {
        public bool IsResolved => true;
        public Guid OrganizationId => organizationId;
        public string OrganizationSlug => "test";
        public Guid ActorId => actorId;
        public Guid MembershipId => membershipId;
        public IReadOnlySet<string> Permissions => permissions;
    }

    private sealed class TestPermissionAuthorizer(bool granted) : IPermissionAuthorizer
    {
        public Task<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken = default) => Task.FromResult(granted);
    }

    private sealed class TestProjectStore : IProjectStore
    {
        public List<Project> Added { get; } = [];
        public Task<PagedResult<Project>> ListAsync(Guid organizationId, int page, int pageSize, string? search, RecordLifecycleFilter lifecycle, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<Project?> FindAsync(Guid organizationId, Guid projectId, CancellationToken cancellationToken) => Task.FromResult<Project?>(null);
        public Task AddAsync(Project project, CancellationToken cancellationToken) { Added.Add(project); return Task.CompletedTask; }
        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingAuditWriter : IAuditWriter
    {
        public List<string> Actions { get; } = [];
        public void Record(string action, AuditTarget target, IReadOnlyDictionary<string, string?>? details = null) => Actions.Add(action);
        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingOutboxWriter : IOutboxWriter
    {
        public List<object> LegacyMessages { get; } = [];
        public List<object> TypedMessages { get; } = [];
        public List<(string OperationKey, int Version)> TypedRegistrations { get; } = [];
        public void Enqueue<T>(T message) where T : notnull => LegacyMessages.Add(message);
        public void Enqueue<T>(T message, string operationKey, int operationVersion) where T : notnull
        {
            TypedMessages.Add(message);
            TypedRegistrations.Add((operationKey, operationVersion));
        }
    }

    private sealed class PassThroughOrganizationCache : IOrganizationCache
    {
        public Task<T> GetOrCreateAsync<T>(ModuleCacheKey key, Func<CancellationToken, Task<T>> factory,
            CancellationToken cancellationToken = default) => factory(cancellationToken);

        public Task InvalidateAsync(ModuleCacheKey key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
