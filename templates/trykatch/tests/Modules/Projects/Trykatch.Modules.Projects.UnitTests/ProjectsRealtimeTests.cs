using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trykatch.Application.Authorization;
using Trykatch.Application.Common;
using Trykatch.Application.Organizations;
using Trykatch.Modules;
using Trykatch.Modules.Projects.Application;
using Trykatch.Modules.Projects.Domain;
using Trykatch.Modules.Projects.Infrastructure;
using Trykatch.Modules.Projects.IntegrationEvents;

namespace Trykatch.Modules.Projects.UnitTests;

public sealed partial class ProjectsCreationExecutionTests
{
    [TestMethod]
    public void RealtimeConsumerHasOneExactCommittedOperationAndReciprocalReadPolicyGrant()
    {
        ProjectsModule module = new();
        ModuleCatalog catalog = new([module]);
        catalog.ModuleIds.ShouldContain("projects");
        ModuleEventConsumerDescriptor consumer = module.Descriptor.EventConsumers.ShouldHaveSingleItem();
        consumer.AcceptedEvents.ShouldBe([new ModuleEventVersion("projects.created", 1)]);
        ModuleOperationDescriptor operation = module.Descriptor.Operations.Single(item => item.OperationKey == consumer.OperationKey);
        operation.ExecutionKind.ShouldBe(ModuleExecutionKind.CommittedEvent);
        operation.RequiredPermissionKeys.ShouldBeEmpty();
        operation.ResourcePolicyKey.ShouldBe("projects");
        ModuleSystemGrantDescriptor grant = module.Descriptor.SystemGrants.ShouldHaveSingleItem();
        grant.AllowedOperationKeys.ShouldBe([operation.OperationKey]);
        grant.AllowedResourceKeys.ShouldBe(["projects"]);
        ModuleRealtimeAudiencePolicyDescriptor policy = module.Descriptor.RealtimeAudiencePolicies.ShouldHaveSingleItem();
        policy.RequiredUserPermissionKeys.ShouldBe(["projects.read"]);
        policy.AllowedEvents.ShouldBe([new ModuleEventVersion("projects.created", 1)]);
        policy.AllowedSystemProducers.ShouldBe([new ModuleRealtimeProducerRef("projects", operation.OperationKey, 1)]);
        grant.AllowedRealtimePolicies.ShouldBe([new ModuleRealtimePolicyRef("projects", policy.PolicyKey, 1)]);
    }

    [TestMethod]
    public async Task RegisteredRealtimeConsumerPublishesOnlyTheCommittedSourceReference()
    {
        RecordingRealtimePublisher publisher = new();
        using ServiceProvider services = RealtimeServices(new TestProjectStore(), publisher, ActorId, true);
        ModuleEventConsumerBinding binding = services.GetServices<ModuleEventConsumerBinding>().ShouldHaveSingleItem();
        Guid message = Guid.NewGuid(), project = Guid.NewGuid();
        Guid systemActor = new ProjectsModule().Descriptor.SystemGrants.Single().AuditActorId;
        await binding.Consumer.HandleAsync(new(message, OrganizationId, systemActor, ActorId,
            "projects.created", 1, DateTimeOffset.UtcNow, null),
            JsonSerializer.SerializeToElement(new ProjectCreated(OrganizationId, project, ActorId), PayloadJson), CancellationToken.None);
        ModuleRealtimeNotification notification = publisher.Messages.ShouldHaveSingleItem();
        notification.ModuleId.ShouldBe("projects");
        notification.EventKey.ShouldBe("projects.created");
        notification.SchemaVersion.ShouldBe(1);
        notification.EventId.ShouldBe(message);
        notification.Audience.Kind.ShouldBe(ModuleRealtimeAudienceKind.Organization);
        notification.Audience.UserId.ShouldBeNull();
        notification.Audience.Resource.ShouldBe(new ModuleRealtimeResourceRef("projects", project));
    }

    [TestMethod]
    public async Task CommittedSourceStillPublishesWhenOrganizationReadsAndWritesAreDisabled()
    {
        RecordingRealtimePublisher publisher = new();
        RealtimeFeatureEvaluator features = new(enabled: false);
        using ServiceProvider services = RealtimeServices(new TestProjectStore(), publisher, ActorId, true, features: features);
        ModuleEventConsumerBinding binding = services.GetServices<ModuleEventConsumerBinding>().ShouldHaveSingleItem();
        Guid message = Guid.NewGuid(), project = Guid.NewGuid();
        Guid systemActor = new ProjectsModule().Descriptor.SystemGrants.Single().AuditActorId;

        await binding.Consumer.HandleAsync(new(message, OrganizationId, systemActor, ActorId,
            "projects.created", 1, DateTimeOffset.UtcNow, null),
            JsonSerializer.SerializeToElement(new ProjectCreated(OrganizationId, project, ActorId), PayloadJson), CancellationToken.None);

        publisher.Messages.ShouldHaveSingleItem().Audience.Resource.ShouldBe(new ModuleRealtimeResourceRef("projects", project));
        features.QueriedKeys.ShouldBeEmpty();
    }

    [TestMethod]
    [DataRow("organization")]
    [DataRow("actor")]
    [DataRow("system-actor")]
    [DataRow("event")]
    [DataRow("schema")]
    [DataRow("project")]
    [DataRow("message")]
    public async Task RealtimeConsumerRejectsIncompatibleSourceWithoutPublishing(string change)
    {
        RecordingRealtimePublisher publisher = new();
        using ServiceProvider services = RealtimeServices(new TestProjectStore(), publisher, ActorId, true);
        IModuleEventConsumer consumer = services.GetServices<ModuleEventConsumerBinding>().Single().Consumer;
        Guid systemActor = new ProjectsModule().Descriptor.SystemGrants.Single().AuditActorId;
        ModuleEventContext context = new(Guid.NewGuid(), OrganizationId, systemActor, ActorId,
            "projects.created", 1, DateTimeOffset.UtcNow, null);
        ProjectCreated source = new(OrganizationId, Guid.NewGuid(), ActorId);
        switch (change)
        {
            case "organization": source = source with { OrganizationId = Guid.NewGuid() }; break;
            case "actor": source = source with { ActorId = Guid.NewGuid() }; break;
            case "system-actor": context = context with { EffectiveActorId = Guid.NewGuid() }; break;
            case "event": context = context with { EventKey = "projects.other" }; break;
            case "schema": context = context with { SchemaVersion = 2 }; break;
            case "project": source = source with { ProjectId = Guid.Empty }; break;
            case "message": context = context with { MessageId = Guid.Empty }; break;
        }
        await Should.ThrowAsync<InvalidOperationException>(() => consumer.HandleAsync(context,
            JsonSerializer.SerializeToElement(source, PayloadJson), CancellationToken.None));
        publisher.Messages.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task CommittedSourcePolicyRequiresTheCurrentProjectAndItsOriginalCreator()
    {
        TestProjectStore store = new();
        Project project = Project.Create(OrganizationId, "Visible project", "Never copied into invalidation", ActorId);
        store.Added.Add(project);
        ModuleDescriptor descriptor = new ProjectsModule().Descriptor;
        Guid systemActor = descriptor.SystemGrants.Single().AuditActorId;
        using ServiceProvider services = RealtimeServices(store, new RecordingRealtimePublisher(), systemActor, false);
        IModuleOperationResourcePolicy policy = services.GetServices<IModuleOperationResourcePolicy>().Single();
        ModuleOperationContext context = new(Guid.NewGuid(), OrganizationId, systemActor, ActorId,
            ModuleExecutionKind.CommittedEvent, descriptor.EventConsumers.Single().OperationKey, 1);
        ProjectCreated source = new(OrganizationId, project.Id, ActorId);
        (await policy.AuthorizeAsync(context, JsonSerializer.SerializeToElement(source, PayloadJson), CancellationToken.None)).ShouldBeTrue();
        (await policy.AuthorizeAsync(context, JsonSerializer.SerializeToElement(source with { ProjectId = Guid.NewGuid() }, PayloadJson), CancellationToken.None)).ShouldBeFalse();
        (await policy.AuthorizeAsync(context with { OriginActorId = Guid.NewGuid() }, JsonSerializer.SerializeToElement(source, PayloadJson), CancellationToken.None)).ShouldBeFalse();
        (await policy.AuthorizeAsync(context with { EffectiveActorId = Guid.NewGuid() }, JsonSerializer.SerializeToElement(source, PayloadJson), CancellationToken.None)).ShouldBeFalse();
    }

    [TestMethod]
    public async Task RecipientReadPolicyRechecksTheSameProjectVisibilityAndPermissionAsReads()
    {
        TestProjectStore store = new();
        Project project = Project.Create(OrganizationId, "Visible project", "Private description", ActorId);
        store.Added.Add(project);
        using ServiceProvider services = RealtimeServices(store, new RecordingRealtimePublisher(), ActorId, true);
        ModuleRealtimeAudiencePolicyBinding binding = services.GetServices<ModuleRealtimeAudiencePolicyBinding>().Single();
        ModuleRealtimeAudience audience = new(binding.Policy, ModuleRealtimeAudienceKind.Organization, Resource: new("projects", project.Id));
        ModuleRealtimeAudienceContext context = new(OrganizationId, ActorId);
        (await binding.Adapter.AuthorizeAsync(context, audience, CancellationToken.None)).ShouldBeTrue();
        (await binding.Adapter.AuthorizeAsync(context with { OrganizationId = Guid.NewGuid() }, audience, CancellationToken.None)).ShouldBeFalse();
        store.Added.Clear();
        (await binding.Adapter.AuthorizeAsync(context, audience, CancellationToken.None)).ShouldBeFalse();
        using ServiceProvider denied = RealtimeServices(store, new RecordingRealtimePublisher(), ActorId, false);
        ModuleRealtimeAudiencePolicyBinding deniedBinding = denied.GetServices<ModuleRealtimeAudiencePolicyBinding>().Single();
        (await deniedBinding.Adapter.AuthorizeAsync(context, audience with { Resource = null }, CancellationToken.None)).ShouldBeFalse();
    }

    [TestMethod]
    public async Task RecipientReadPolicyRechecksTheCurrentReadsFeatureBeforeLookingUpAnyProject()
    {
        CountingProjectStore store = new();
        Project project = Project.Create(OrganizationId, "Visible project", "Private description", ActorId);
        store.Added.Add(project);
        RealtimeFeatureEvaluator features = new(enabled: true);
        using ServiceProvider services = RealtimeServices(store, new RecordingRealtimePublisher(), ActorId, true, features: features);
        ModuleRealtimeAudiencePolicyBinding binding = services.GetServices<ModuleRealtimeAudiencePolicyBinding>().Single();
        ModuleRealtimeAudience resourceAudience = new(binding.Policy, ModuleRealtimeAudienceKind.Resource,
            Resource: new("projects", project.Id));
        ModuleRealtimeAudience organizationAudience = new(binding.Policy, ModuleRealtimeAudienceKind.Organization);
        ModuleRealtimeAudienceContext context = new(OrganizationId, ActorId);

        (await binding.Adapter.AuthorizeAsync(context, resourceAudience, CancellationToken.None)).ShouldBeTrue();
        store.FindCalls.ShouldBe(1);
        features.QueriedKeys.ShouldBe([ProjectFeatures.Reads]);

        features.Enabled = false;
        (await binding.Adapter.AuthorizeAsync(context, resourceAudience, CancellationToken.None)).ShouldBeFalse();
        (await binding.Adapter.AuthorizeAsync(context, organizationAudience, CancellationToken.None)).ShouldBeFalse();
        store.FindCalls.ShouldBe(1);
        features.QueriedKeys.ShouldBe([ProjectFeatures.Reads, ProjectFeatures.Reads, ProjectFeatures.Reads]);
    }

    [TestMethod]
    public async Task RecipientReadPolicyRejectsPermissionAndActorMismatchesBeforeCheckingFeaturesOrStore()
    {
        CountingProjectStore store = new();
        RealtimeFeatureEvaluator features = new(enabled: true);
        ModuleRealtimeAudience audience = new(new("projects", "projects.read", 1), ModuleRealtimeAudienceKind.Organization,
            Resource: new("projects", Guid.NewGuid()));
        ModuleRealtimeAudienceContext context = new(OrganizationId, ActorId);

        using ServiceProvider denied = RealtimeServices(store, new RecordingRealtimePublisher(), ActorId, false, features: features);
        ModuleRealtimeAudiencePolicyBinding deniedBinding = denied.GetServices<ModuleRealtimeAudiencePolicyBinding>().Single();
        (await deniedBinding.Adapter.AuthorizeAsync(context, audience, CancellationToken.None)).ShouldBeFalse();

        using ServiceProvider allowed = RealtimeServices(store, new RecordingRealtimePublisher(), ActorId, true, features: features);
        ModuleRealtimeAudiencePolicyBinding allowedBinding = allowed.GetServices<ModuleRealtimeAudiencePolicyBinding>().Single();
        (await allowedBinding.Adapter.AuthorizeAsync(context with { ActorId = Guid.NewGuid() }, audience, CancellationToken.None)).ShouldBeFalse();

        features.QueriedKeys.ShouldBeEmpty();
        store.FindCalls.ShouldBe(0);
    }

    private static ServiceProvider RealtimeServices(IProjectStore store, RecordingRealtimePublisher publisher, Guid actor, bool canRead,
        RealtimeFeatureEvaluator? features = null)
    {
        ServiceCollection services = new();
        new ProjectsModule().Register(services, new ConfigurationBuilder().Build());
        services.AddSingleton<IProjectStore>(store);
        services.AddSingleton<IModuleRealtimePublisher>(publisher);
        services.AddSingleton<Trykatch.Modules.IOrganizationFeatureEvaluator>(features ?? new RealtimeFeatureEvaluator(enabled: true));
        services.AddSingleton<IOrganizationContext>(new TestOrganizationContext(OrganizationId, actor, MembershipId,
            new HashSet<string>(canRead ? ["projects.read"] : [], StringComparer.Ordinal)));
        services.AddSingleton<IPermissionAuthorizer>(new TestPermissionAuthorizer(canRead));
        return services.BuildServiceProvider();
    }

    private sealed class RealtimeFeatureEvaluator(bool enabled) : Trykatch.Modules.IOrganizationFeatureEvaluator
    {
        public bool Enabled { get; set; } = enabled;
        public List<string> QueriedKeys { get; } = [];

        public Task<bool> IsEnabledAsync(string featureKey, CancellationToken ct)
        {
            QueriedKeys.Add(featureKey);
            return Task.FromResult(Enabled);
        }
    }

    private sealed class CountingProjectStore : IProjectStore
    {
        public List<Project> Added { get; } = [];
        public int FindCalls { get; private set; }

        public Task<PagedResult<Project>> ListAsync(Guid organizationId, int page, int pageSize, string? search,
            RecordLifecycleFilter lifecycle, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Project?> FindAsync(Guid organizationId, Guid projectId, CancellationToken cancellationToken)
        {
            FindCalls++;
            return Task.FromResult(Added.SingleOrDefault(project => project.OrganizationId == organizationId && project.Id == projectId));
        }

        public Task AddAsync(Project project, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveChangesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingRealtimePublisher : IModuleRealtimePublisher
    {
        public List<ModuleRealtimeNotification> Messages { get; } = [];
        public Task PublishAsync(ModuleRealtimeNotification notification, CancellationToken cancellationToken = default)
        {
            Messages.Add(notification);
            return Task.CompletedTask;
        }
    }
}
