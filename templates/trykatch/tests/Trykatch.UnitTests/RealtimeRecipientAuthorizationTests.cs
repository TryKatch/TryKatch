using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trykatch.Application.Organizations;
using Trykatch.Application.Realtime;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Realtime;
using Trykatch.Modules;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class RealtimeRecipientAuthorizationTests
{
    private static readonly Guid OrganizationId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly Guid ResourceId = Guid.NewGuid();
    private static readonly Guid MembershipId = Guid.NewGuid();

    [TestMethod]
    public async Task RecipientMustHaveResolvedNonzeroOrganizationActorAndHumanMembership()
    {
        IOrganizationContext[] invalidContexts =
        [
            new OrganizationContext(),
            Context(organizationId: Guid.Empty),
            Context(actorId: Guid.Empty),
            Context(membershipId: Guid.Empty),
            SystemContext()
        ];

        foreach (IOrganizationContext context in invalidContexts)
        {
            MutablePolicy adapter = new();
            RealtimeAudienceAuthorization authorizer = CreateAuthorizer(context, adapter);

            (await authorizer.AuthorizeAsync(Audience(ModuleRealtimeAudienceKind.Organization))).ShouldBeFalse();
            adapter.Calls.ShouldBeEmpty();
        }
    }

    [TestMethod]
    public async Task RequiredCurrentPermissionIsCheckedBeforeModulePolicyAdapter()
    {
        MutablePolicy adapter = new();
        RealtimeAudienceAuthorization denied = CreateAuthorizer(Context(permissions: new HashSet<string>(StringComparer.Ordinal)), adapter);

        (await denied.AuthorizeAsync(Audience(ModuleRealtimeAudienceKind.Organization))).ShouldBeFalse();
        adapter.Calls.ShouldBeEmpty();

        RealtimeAudienceAuthorization allowed = CreateAuthorizer(Context(), adapter);
        (await allowed.AuthorizeAsync(Audience(ModuleRealtimeAudienceKind.Organization))).ShouldBeTrue();
        adapter.Calls.Count.ShouldBe(1);
        adapter.Allow = false;
        (await allowed.AuthorizeAsync(Audience(ModuleRealtimeAudienceKind.Organization))).ShouldBeFalse();
    }

    [TestMethod]
    public async Task EachRegisteredAudienceUsesCurrentContextAndModulePolicyDecision()
    {
        MutablePolicy adapter = new() { VisibleResources = [ResourceId] };
        RealtimeAudienceAuthorization authorizer = CreateAuthorizer(Context(), adapter);
        ModuleRealtimeResourceRef resource = new("projects", ResourceId);
        ModuleRealtimeAudience[] audiences =
        [
            new(Policy(), ModuleRealtimeAudienceKind.Organization, Resource: resource),
            new(Policy(), ModuleRealtimeAudienceKind.User, ActorId, resource),
            new(Policy(), ModuleRealtimeAudienceKind.Resource, Resource: resource)
        ];

        foreach (ModuleRealtimeAudience audience in audiences)
            (await authorizer.AuthorizeAsync(audience)).ShouldBeTrue();

        adapter.Calls.Select(call => call.Context).ShouldAllBe(context =>
            context.OrganizationId == OrganizationId && context.ActorId == ActorId);
        adapter.Calls.Select(call => call.Audience.Kind)
            .ShouldBe([ModuleRealtimeAudienceKind.Organization, ModuleRealtimeAudienceKind.User, ModuleRealtimeAudienceKind.Resource]);
        adapter.Calls.Select(call => call.Audience.Resource?.ResourceId).ShouldAllBe(id => id == ResourceId);
    }

    [TestMethod]
    public async Task ExplicitUserAudienceIsLimitedToCurrentActorAndAdapterRunsOnEveryAttempt()
    {
        MutablePolicy adapter = new() { VisibleResources = [ResourceId] };
        RealtimeAudienceAuthorization authorizer = CreateAuthorizer(Context(), adapter);
        ModuleRealtimeAudience resource = new(Policy(), ModuleRealtimeAudienceKind.Resource,
            Resource: new("projects", ResourceId));

        (await authorizer.AuthorizeAsync(new ModuleRealtimeAudience(Policy(), ModuleRealtimeAudienceKind.User, ActorId))).ShouldBeTrue();
        (await authorizer.AuthorizeAsync(new ModuleRealtimeAudience(Policy(), ModuleRealtimeAudienceKind.User, Guid.NewGuid()))).ShouldBeFalse();
        (await authorizer.AuthorizeAsync(resource)).ShouldBeTrue();
        adapter.VisibleResources.Clear();
        (await authorizer.AuthorizeAsync(resource)).ShouldBeFalse();

        adapter.Calls.Count.ShouldBe(3);
        adapter.Calls[0].Audience.UserId.ShouldBe(ActorId);
        adapter.Calls[1].Audience.Resource?.ResourceId.ShouldBe(ResourceId);
    }

    [TestMethod]
    public async Task MalformedAndUnregisteredAudienceSelectorsAreDeniedWithoutCallingAdapter()
    {
        MutablePolicy adapter = new();
        RealtimeAudienceAuthorization authorizer = CreateAuthorizer(Context(), adapter);
        ModuleRealtimeAudience[] invalid =
        [
            Audience(ModuleRealtimeAudienceKind.Organization) with { Policy = Policy() with { OwnerModuleId = "missing" } },
            Audience(ModuleRealtimeAudienceKind.Organization) with { Policy = Policy() with { PolicyVersion = 2 } },
            new(Policy(), ModuleRealtimeAudienceKind.Organization, Resource: new("documents", Guid.NewGuid())),
            new(Policy(), ModuleRealtimeAudienceKind.Resource, Resource: new("projects.*", Guid.NewGuid())),
            new(Policy(), (ModuleRealtimeAudienceKind)999),
            new(Policy(), ModuleRealtimeAudienceKind.Organization, Guid.NewGuid()),
            null!
        ];

        foreach (ModuleRealtimeAudience audience in invalid)
            (await authorizer.AuthorizeAsync(audience)).ShouldBeFalse();
        adapter.Calls.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task NotificationRequiresInstalledOwnerEventSchemaAndAudiencePolicy()
    {
        MutablePolicy adapter = new();
        RealtimeAudienceAuthorization authorizer = CreateAuthorizer(Context(), adapter);
        ModuleRealtimeNotification valid = Notification(Audience(ModuleRealtimeAudienceKind.Organization));

        (await authorizer.AuthorizeAsync(valid)).ShouldBeTrue();
        ModuleRealtimeNotification[] invalid =
        [
            valid with { ModuleId = "reports", EventKey = "reports.created" },
            valid with { SchemaVersion = 2 },
            valid with { Audience = valid.Audience with { Policy = Policy() with { PolicyVersion = 2 } } },
            valid with { Audience = valid.Audience with { Resource = new("documents", Guid.NewGuid()) } },
            valid with { Audience = valid.Audience with { Kind = (ModuleRealtimeAudienceKind)999 } }
        ];

        foreach (ModuleRealtimeNotification notification in invalid)
            (await authorizer.AuthorizeAsync(notification)).ShouldBeFalse();
        adapter.Calls.Count.ShouldBe(1);
    }

    [TestMethod]
    public async Task RetainedNotificationsAreSuppressedWhenOwnerModuleOrPolicyVersionIsGone()
    {
        ModuleRealtimeNotification retained = Notification(Audience(ModuleRealtimeAudienceKind.Organization));
        MutablePolicy absentAdapter = new();
        RealtimeAudienceAuthorization absent = CreateAuthorizer(Context(), absentAdapter, modules: []);
        (await absent.AuthorizeAsync(retained)).ShouldBeFalse();
        absentAdapter.Calls.ShouldBeEmpty();

        MutablePolicy changedAdapter = new();
        RealtimeAudienceAuthorization changed = CreateAuthorizer(Context(), changedAdapter, SourceModule(policyVersion: 2));
        (await changed.AuthorizeAsync(retained)).ShouldBeFalse();
        changedAdapter.Calls.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task AdapterFailuresAndCancellationPropagateToCaller()
    {
        MutablePolicy adapter = new() { Failure = new AdapterFailureException() };
        RealtimeAudienceAuthorization authorizer = CreateAuthorizer(Context(), adapter);
        await Should.ThrowAsync<AdapterFailureException>(() => authorizer.AuthorizeAsync(Audience(ModuleRealtimeAudienceKind.Organization)));

        adapter.Failure = null;
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() =>
            authorizer.AuthorizeAsync(Audience(ModuleRealtimeAudienceKind.Organization), cancellation.Token));
    }

    private static RealtimeAudienceAuthorization CreateAuthorizer(
        IOrganizationContext context,
        MutablePolicy adapter,
        ModuleDescriptor? descriptor = null,
        IModule[]? modules = null)
    {
        descriptor ??= SourceModule();
        IModule[] installed = modules ?? [new StubModule(descriptor)];
        ModuleRealtimeAudiencePolicyBinding[] bindings = installed
            .SelectMany(module => module.Descriptor.RealtimeAudiencePolicies)
            .Select(policy => new ModuleRealtimeAudiencePolicyBinding(
                new(policy.OwnerModuleId, policy.PolicyKey, policy.PolicyVersion), adapter))
            .ToArray();
        RealtimeAudienceRegistry registry = new(new ModuleCatalog(installed), bindings);
        return new(registry, context);
    }

    private static ModuleRealtimeAudience Audience(ModuleRealtimeAudienceKind kind) => new(Policy(), kind);

    private static ModuleRealtimeNotification Notification(ModuleRealtimeAudience audience) =>
        new("projects", "projects.created", 1, Guid.NewGuid(), audience);

    private static ModuleRealtimePolicyRef Policy(int version = 1) => new("projects", "projects.realtime-read", version);

    private static OrganizationContext Context(
        Guid? organizationId = null,
        Guid? actorId = null,
        Guid? membershipId = null,
        IReadOnlySet<string>? permissions = null)
    {
        OrganizationContext context = new();
        ((IOrganizationContextInitializer)context).Initialize(new(
            organizationId ?? OrganizationId,
            "fixture",
            actorId ?? ActorId,
            membershipId ?? MembershipId,
            permissions ?? new HashSet<string>(["projects.read"], StringComparer.Ordinal)));
        return context;
    }

    private static OrganizationContext SystemContext()
    {
        OrganizationContext context = new();
        context.InitializeSystem(new(OrganizationId, "fixture", ActorId,
            new HashSet<string>(["projects.read"], StringComparer.Ordinal)));
        return context;
    }

    private static ModuleDescriptor SourceModule(int policyVersion = 1) => new(
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
            new("projects", "projects.realtime-read", policyVersion,
                [ModuleRealtimeAudienceKind.Organization, ModuleRealtimeAudienceKind.User, ModuleRealtimeAudienceKind.Resource],
                ["projects"], ["projects.read"], [new("projects.created", 1)], [])
        ]
    };

    private sealed class StubModule(ModuleDescriptor descriptor) : IModule
    {
        public ModuleDescriptor Descriptor { get; } = descriptor;
        public void Register(IServiceCollection services, IConfiguration configuration) { }
    }

    private sealed class MutablePolicy : IModuleRealtimeAudiencePolicy
    {
        public HashSet<Guid> VisibleResources { get; set; } = [];
        public bool Allow { get; set; } = true;
        public Exception? Failure { get; set; }
        public List<(ModuleRealtimeAudienceContext Context, ModuleRealtimeAudience Audience)> Calls { get; } = [];

        public Task<bool> AuthorizeAsync(ModuleRealtimeAudienceContext context, ModuleRealtimeAudience audience,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null) throw Failure;
            Calls.Add((context, audience));
            if (context.OrganizationId != OrganizationId || context.ActorId != ActorId || !Allow)
                return Task.FromResult(false);
            if (audience.Resource is not null && !VisibleResources.Contains(audience.Resource.ResourceId))
                return Task.FromResult(false);
            return Task.FromResult(true);
        }
    }

    private sealed class AdapterFailureException : Exception { }
}
