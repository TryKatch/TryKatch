using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trykatch.Application.Authorization;
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

    private static ServiceProvider RealtimeServices(TestProjectStore store, RecordingRealtimePublisher publisher, Guid actor, bool canRead)
    {
        ServiceCollection services = new();
        new ProjectsModule().Register(services, new ConfigurationBuilder().Build());
        services.AddSingleton<IProjectStore>(store);
        services.AddSingleton<IModuleRealtimePublisher>(publisher);
        services.AddSingleton<IOrganizationContext>(new TestOrganizationContext(OrganizationId, actor, MembershipId,
            new HashSet<string>(canRead ? ["projects.read"] : [], StringComparer.Ordinal)));
        services.AddSingleton<IPermissionAuthorizer>(new TestPermissionAuthorizer(canRead));
        return services.BuildServiceProvider();
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
