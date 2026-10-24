using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trykatch.Modules;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class RealtimeAudienceTests
{
    [TestMethod]
    public void PublicContractsAreProviderNeutralAndRedactAudienceIdentifiers()
    {
        typeof(IModuleRealtimePublisher).GetMethod(nameof(IModuleRealtimePublisher.PublishAsync))!.ReturnType
            .ShouldBe(typeof(Task));
        typeof(IModuleRealtimeAudiencePolicy).GetMethod(nameof(IModuleRealtimeAudiencePolicy.AuthorizeAsync))!.ReturnType
            .ShouldBe(typeof(Task<bool>));

        Guid userId = Guid.NewGuid();
        Guid resourceId = Guid.NewGuid();
        ModuleRealtimeAudience audience = new(Policy(), ModuleRealtimeAudienceKind.Resource,
            Resource: new("projects", resourceId));
        ModuleRealtimeNotification notification = new("projects", "projects.created", 1, Guid.NewGuid(), audience);
        ModuleRealtimeAudienceContext context = new(Guid.NewGuid(), userId);

        audience.ToString().ShouldNotContain(resourceId.ToString());
        notification.ToString().ShouldNotContain(resourceId.ToString());
        context.ToString().ShouldNotContain(userId.ToString());
        new ModuleRealtimeResourceRef("projects", resourceId).ToString().ShouldNotContain(resourceId.ToString());
    }

    [TestMethod]
    [DataRow("organization-user")]
    [DataRow("user-missing-id")]
    [DataRow("user-empty-id")]
    [DataRow("resource-missing-ref")]
    [DataRow("resource-empty-id")]
    [DataRow("resource-user")]
    [DataRow("unknown-kind")]
    [DataRow("invalid-policy-key")]
    [DataRow("invalid-policy-version")]
    [DataRow("invalid-resource-key")]
    public void AudienceSelectorsMustMatchTheirDeclaredKind(string selector)
    {
        ModuleRealtimeAudience audience = selector switch
        {
            "organization-user" => new(Policy(), ModuleRealtimeAudienceKind.Organization, Guid.NewGuid()),
            "user-missing-id" => new(Policy(), ModuleRealtimeAudienceKind.User),
            "user-empty-id" => new(Policy(), ModuleRealtimeAudienceKind.User, Guid.Empty),
            "resource-missing-ref" => new(Policy(), ModuleRealtimeAudienceKind.Resource),
            "resource-empty-id" => new(Policy(), ModuleRealtimeAudienceKind.Resource, Resource: new("projects", Guid.Empty)),
            "resource-user" => new(Policy(), ModuleRealtimeAudienceKind.Resource, Guid.NewGuid(), new("projects", Guid.NewGuid())),
            "invalid-policy-key" => new(Policy() with { PolicyKey = "projects.*" }, ModuleRealtimeAudienceKind.Organization),
            "invalid-policy-version" => new(Policy() with { PolicyVersion = 0 }, ModuleRealtimeAudienceKind.Organization),
            "invalid-resource-key" => new(Policy(), ModuleRealtimeAudienceKind.Resource,
                Resource: new("projects.*", Guid.NewGuid())),
            _ => new(Policy(), (ModuleRealtimeAudienceKind)999)
        };

        Should.Throw<InvalidOperationException>(() => ModuleRealtimeRegistrationRules.ValidateAudience(audience));
    }

    [TestMethod]
    public void AudienceAllowsOneWellFormedSelectorPerKind()
    {
        Should.NotThrow(() => ModuleRealtimeRegistrationRules.ValidateAudience(
            new(Policy(), ModuleRealtimeAudienceKind.Organization)));
        Should.NotThrow(() => ModuleRealtimeRegistrationRules.ValidateAudience(
            new(Policy(), ModuleRealtimeAudienceKind.User, Guid.NewGuid())));
        Should.NotThrow(() => ModuleRealtimeRegistrationRules.ValidateAudience(
            new(Policy(), ModuleRealtimeAudienceKind.Resource, Resource: new("projects", Guid.NewGuid()))));
        Should.NotThrow(() => ModuleRealtimeRegistrationRules.ValidateAudience(
            new(Policy(), ModuleRealtimeAudienceKind.Organization, Resource: new("projects", Guid.NewGuid()))));
        Should.NotThrow(() => ModuleRealtimeRegistrationRules.ValidateAudience(
            new(Policy(), ModuleRealtimeAudienceKind.User, Guid.NewGuid(), new("projects", Guid.NewGuid()))));
    }

    [TestMethod]
    [DataRow("empty-module")]
    [DataRow("empty-event")]
    [DataRow("invalid-event-key")]
    [DataRow("invalid-version")]
    [DataRow("empty-event-id")]
    [DataRow("null-audience")]
    public void NotificationsRequireBoundedStableIdentityAndAudience(string change)
    {
        ModuleRealtimeNotification notification = new("projects", "projects.created", 1, Guid.NewGuid(),
            new(Policy(), ModuleRealtimeAudienceKind.Organization));
        notification = change switch
        {
            "empty-module" => notification with { ModuleId = "" },
            "empty-event" => notification with { EventKey = "" },
            "invalid-event-key" => notification with { EventKey = "projects.*" },
            "invalid-version" => notification with { SchemaVersion = 0 },
            "empty-event-id" => notification with { EventId = Guid.Empty },
            "null-audience" => notification with { Audience = null! },
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };

        Should.Throw<InvalidOperationException>(() => ModuleRealtimeRegistrationRules.ValidateNotification(notification));
    }

    [TestMethod]
    public void NotificationAcceptsAWellFormedReferenceInvalidation()
    {
        Should.NotThrow(() => ModuleRealtimeRegistrationRules.ValidateNotification(new(
            "projects", "projects.created", 1, Guid.NewGuid(), new(Policy(), ModuleRealtimeAudienceKind.Organization))));
    }

    [TestMethod]
    [DataRow("empty-module")]
    [DataRow("unknown-event-version")]
    [DataRow("unknown-resource")]
    [DataRow("missing-permission")]
    [DataRow("foreign-permission")]
    [DataRow("organization-without-permission")]
    [DataRow("duplicate-policy")]
    [DataRow("wrong-owner")]
    [DataRow("too-many-events")]
    [DataRow("invalid-policy-key")]
    [DataRow("policy-version")]
    [DataRow("wildcard-event")]
    [DataRow("duplicate-resource")]
    [DataRow("duplicate-event")]
    [DataRow("duplicate-audience")]
    [DataRow("duplicate-producer")]
    [DataRow("unknown-audience")]
    public void CatalogRejectsInvalidRealtimePolicyDeclarations(string change)
    {
        ModuleDescriptor module = SourceModule();
        ModuleRealtimeAudiencePolicyDescriptor policy = module.RealtimeAudiencePolicies.Single();
        switch (change)
        {
            case "empty-module": module = module with { Id = "" }; break;
            case "unknown-event-version": module = module with { RealtimeAudiencePolicies = [policy with { AllowedEvents = [new("projects.created", 2)] }] }; break;
            case "unknown-resource": module = module with { RealtimeAudiencePolicies = [policy with { AllowedResourceKeys = ["unknown"] }] }; break;
            case "missing-permission": module = module with { RealtimeAudiencePolicies = [policy with { RequiredUserPermissionKeys = ["projects.missing"] }] }; break;
            case "foreign-permission": module = module with { RealtimeAudiencePolicies = [policy with { RequiredUserPermissionKeys = ["reports.read"] }] }; break;
            case "organization-without-permission": module = module with { RealtimeAudiencePolicies = [policy with { RequiredUserPermissionKeys = [] }] }; break;
            case "duplicate-policy": module = module with { RealtimeAudiencePolicies = [policy, policy] }; break;
            case "wrong-owner": module = module with { RealtimeAudiencePolicies = [policy with { OwnerModuleId = "other" }] }; break;
            case "too-many-events": module = module with { RealtimeAudiencePolicies = [policy with { AllowedEvents = Enumerable.Range(0, 101).Select(index => new ModuleEventVersion($"projects.event{index}", 1)).ToArray() }] }; break;
            case "invalid-policy-key": module = module with { RealtimeAudiencePolicies = [policy with { PolicyKey = "projects.*" }] }; break;
            case "policy-version": module = module with { RealtimeAudiencePolicies = [policy with { PolicyVersion = 0 }] }; break;
            case "wildcard-event": module = module with { RealtimeAudiencePolicies = [policy with { AllowedEvents = [new("projects.*", 1)] }] }; break;
            case "duplicate-resource": module = module with { RealtimeAudiencePolicies = [policy with { AllowedResourceKeys = ["projects", "projects"] }] }; break;
            case "duplicate-event": module = module with { RealtimeAudiencePolicies = [policy with { AllowedEvents = [new("projects.created", 1), new("projects.created", 1)] }] }; break;
            case "duplicate-audience": module = module with { RealtimeAudiencePolicies = [policy with { AllowedAudiences = [ModuleRealtimeAudienceKind.User, ModuleRealtimeAudienceKind.User] }] }; break;
            case "duplicate-producer": module = module with { RealtimeAudiencePolicies = [policy with { AllowedSystemProducers = [new("reports", "reports.refresh", 1), new("reports", "reports.refresh", 1)] }] }; break;
            case "unknown-audience": module = module with { RealtimeAudiencePolicies = [policy with { AllowedAudiences = [(ModuleRealtimeAudienceKind)999] }] }; break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }

        IModule[] installed = change == "foreign-permission"
            ? [new StubModule(module), new StubModule(new ModuleDescriptor("reports", "Reports", "1.0.0", "Fixture", [], [], ModuleCapabilities.Api, [])
                { Permissions = [new("reports.read", "Read reports", "Read report data.")] })]
            : [new StubModule(module)];
        Should.Throw<InvalidOperationException>(() => new ModuleCatalog(installed));
    }

    [TestMethod]
    public void CatalogAcceptsAnInstalledPolicyAndHookRunsOnConstruction()
    {
        Should.NotThrow(() => new ModuleCatalog([new StubModule(SourceModule())]));
    }

    [TestMethod]
    public void RealtimeValidationDoesNotCapExistingUnrelatedModuleMetadata()
    {
        ModuleDescriptor module = SourceModule() with
        {
            Permissions = Enumerable.Range(0, 100)
                .Select(index => new PermissionDescriptor($"projects.extra-{index}", $"Extra {index}", "Fixture."))
                .Append(new("projects.read", "Read projects", "Read project data."))
                .ToArray()
        };

        Should.NotThrow(() => new ModuleCatalog([new StubModule(module)]));
    }

    [TestMethod]
    [DataRow("unknown-policy")]
    [DataRow("duplicate-binding")]
    [DataRow("missing-binding")]
    [DataRow("null-adapter")]
    public void ExplicitPolicyBindingsMustExactlyMatchInstalledDeclarations(string change)
    {
        ModuleDescriptor module = SourceModule();
        ModuleRealtimePolicyRef policy = Policy();
        ModuleRealtimeAudiencePolicyBinding binding = new(policy, new AllowAllPolicy());
        IReadOnlyCollection<ModuleRealtimeAudiencePolicyBinding> bindings = change switch
        {
            "unknown-policy" => [binding with { Policy = policy with { PolicyVersion = 2 } }],
            "duplicate-binding" => [binding, binding],
            "missing-binding" => [],
            "null-adapter" => [new(binding.Policy, null!)],
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };

        Should.Throw<InvalidOperationException>(() => ModuleRealtimeRegistrationRules.ValidateBindings([module], bindings));
    }

    [TestMethod]
    public void ExplicitPolicyBindingMustCoverEveryInstalledPolicyExactlyOnce()
    {
        Should.NotThrow(() => ModuleRealtimeRegistrationRules.ValidateBindings(
            [SourceModule()], [new(Policy(), new AllowAllPolicy())]));
    }

    [TestMethod]
    public void UserAndResourcePoliciesMayRelyOnAnExplicitAdapterWithoutRecipientPermissions()
    {
        ModuleDescriptor source = SourceModule();
        ModuleRealtimeAudiencePolicyDescriptor policy = source.RealtimeAudiencePolicies.Single() with
        {
            AllowedAudiences = [ModuleRealtimeAudienceKind.User, ModuleRealtimeAudienceKind.Resource],
            RequiredUserPermissionKeys = []
        };
        source = source with { RealtimeAudiencePolicies = [policy] };

        Should.NotThrow(() => ModuleRealtimeRegistrationRules.ValidateBindings(
            [source], [new(Policy(), new AllowAllPolicy())]));
    }

    [TestMethod]
    [DataRow("missing-source-consent")]
    [DataRow("missing-producer-grant")]
    [DataRow("wrong-operation-version")]
    [DataRow("scheduled-operation")]
    [DataRow("missing-dependency")]
    public void CommittedEventProducerNeedsExactReciprocalConsentAndGrant(string change)
    {
        ModuleRealtimeProducerRef producer = new("reports", "reports.refresh", 1);
        ModuleDescriptor source = SourceModule(change == "missing-source-consent" ? null : producer);
        ModuleDescriptor reports = ProducerModule(change, Policy());

        Should.Throw<InvalidOperationException>(() => new ModuleCatalog([new StubModule(source), new StubModule(reports)]));
    }

    [TestMethod]
    public void CommittedEventProducerIsAcceptedWithExactReciprocalConsentAndGrant()
    {
        ModuleRealtimePolicyRef policy = Policy();
        ModuleRealtimeProducerRef producer = new("reports", "reports.refresh", 1);
        Should.NotThrow(() => new ModuleCatalog([
            new StubModule(SourceModule(producer)), new StubModule(ProducerModule("valid", policy))]));
    }

    private static ModuleRealtimePolicyRef Policy() => new("projects", "projects.realtime-read", 1);

    private static ModuleDescriptor SourceModule(ModuleRealtimeProducerRef? producer = null) => new(
        "projects", "Projects", "1.0.0", "Fixture", [], [],
        ModuleCapabilities.Api | ModuleCapabilities.Data | ModuleCapabilities.BackgroundWork, [])
    {
        DefaultDataOwnership = ModuleDataOwnership.Organization,
        DataResources = [new("projects", "app", "projects", ModuleDataOwnership.Organization,
            IsolationPolicy: "projects_isolation")],
        Permissions = [new("projects.read", "Read projects", "Read project data.")],
        IntegrationEvents = [new("projects.created", 1, "projects", typeof(string), _ => "", 1024)],
        RealtimeAudiencePolicies =
        [
            new("projects", "projects.realtime-read", 1,
                [ModuleRealtimeAudienceKind.Organization, ModuleRealtimeAudienceKind.User, ModuleRealtimeAudienceKind.Resource],
                ["projects"], ["projects.read"], [new("projects.created", 1)], producer is null ? [] : [producer])
        ]
    };

    private static ModuleDescriptor ProducerModule(string change, ModuleRealtimePolicyRef policy) => new(
        "reports", "Reports", "1.0.0", "Fixture", change == "missing-dependency" ? [] : ["projects"], [],
        ModuleCapabilities.Api | ModuleCapabilities.Data | ModuleCapabilities.BackgroundWork, [])
    {
        DefaultDataOwnership = ModuleDataOwnership.Organization,
        DataResources = [new("reports", "app", "reports", ModuleDataOwnership.Organization,
            IsolationPolicy: "reports_isolation")],
        Permissions = [new("reports.read", "Read reports", "Read report data.")],
        Operations = [new("reports", "reports.refresh", change == "wrong-operation-version" ? 2 : 1,
            change == "scheduled-operation" ? ModuleExecutionKind.ScheduledSystem : ModuleExecutionKind.CommittedEvent,
            "reports.refresh-payload", 1, [], "reports", "reports.refresh-grant", 1024)],
        SystemGrants =
        [
            new ModuleSystemGrantDescriptor("reports.refresh-grant", 1, "reports", Guid.NewGuid(),
                ["reports.refresh"], ["reports"])
            {
                AllowedRealtimePolicies = change == "missing-producer-grant" ? [] : [policy]
            }
        ]
    };

    private sealed class StubModule(ModuleDescriptor descriptor) : IModule
    {
        public ModuleDescriptor Descriptor { get; } = descriptor;
        public void Register(IServiceCollection services, IConfiguration configuration) { }
    }

    private sealed class AllowAllPolicy : IModuleRealtimeAudiencePolicy
    {
        public Task<bool> AuthorizeAsync(ModuleRealtimeAudienceContext context, ModuleRealtimeAudience audience,
            CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
