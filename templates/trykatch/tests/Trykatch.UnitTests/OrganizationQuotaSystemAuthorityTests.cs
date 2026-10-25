using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trykatch.Infrastructure.Execution;
using Trykatch.Infrastructure.Quotas;
using Trykatch.Modules;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class OrganizationQuotaSystemAuthorityTests
{
    private static readonly Guid OrganizationId = Guid.Parse("d37f82bf-c036-4b58-8cd3-a9e1d4b42386");
    private static readonly Guid AuditActorId = Guid.Parse("10d0a994-72a5-4f0c-b61c-753d8d49bc7a");
    private static readonly Guid OriginActorId = Guid.Parse("dd5fa3a1-38bb-4c82-9d17-2fdadbbcae44");
    private const string OwnerModuleId = "documents";
    private const string ResourceKey = "documents.storage-bytes";
    private const string OperationKey = "documents.inventory";
    private const string GrantKey = "documents.maintenance";
    private const string BusinessResourceKey = "documents";

    [TestMethod]
    public void CurrentScheduledGrantExplicitlyAllowsItsOwnedQuotaResource()
    {
        AuthorityFixture fixture = CreateFixture(allowedQuotaResources: [ResourceKey]);

        QuotaSystemAuthority.IsAllowed(fixture.Work, fixture.Catalog, OrganizationId, AuditActorId,
            OwnerModuleId, ResourceKey).ShouldBeTrue();
    }

    [TestMethod]
    public void BusinessResourceAllowanceDoesNotGrantQuotaAuthority()
    {
        AuthorityFixture fixture = CreateFixture(allowedQuotaResources: []);

        fixture.Grant.AllowedResourceKeys.ShouldContain(BusinessResourceKey);
        QuotaSystemAuthority.IsAllowed(fixture.Work, fixture.Catalog, OrganizationId, AuditActorId,
            OwnerModuleId, ResourceKey).ShouldBeFalse();
    }

    [TestMethod]
    public void UninitializedOrMissingWorkProvenanceIsDenied()
    {
        AuthorityFixture fixture = CreateFixture(allowedQuotaResources: [ResourceKey]);
        ModuleWorkExecutionContext uninitialized = new();

        QuotaSystemAuthority.IsAllowed(uninitialized, fixture.Catalog, OrganizationId, AuditActorId,
            OwnerModuleId, ResourceKey).ShouldBeFalse();

        ModuleWorkExecutionContext missingIdentity = new();
        missingIdentity.Initialize(null!, fixture.Operation);
        QuotaSystemAuthority.IsAllowed(missingIdentity, fixture.Catalog, OrganizationId, AuditActorId,
            OwnerModuleId, ResourceKey).ShouldBeFalse();

        ModuleWorkExecutionContext missingOperation = new();
        missingOperation.Initialize(fixture.Identity, null!);
        QuotaSystemAuthority.IsAllowed(missingOperation, fixture.Catalog, OrganizationId, AuditActorId,
            OwnerModuleId, ResourceKey).ShouldBeFalse();
    }

    [TestMethod]
    [DataRow("grant-key")]
    [DataRow("grant-version")]
    [DataRow("catalog-version")]
    public void StaleGrantProvenanceIsDenied(string mutation)
    {
        AuthorityFixture fixture = CreateFixture(allowedQuotaResources: [ResourceKey]);
        ModuleExecutionIdentity stale = mutation switch
        {
            "grant-key" => fixture.Identity with { GrantKey = "documents.previous-maintenance" },
            "grant-version" => fixture.Identity with { GrantVersion = 2 },
            "catalog-version" => fixture.Identity with { CatalogVersion = ModuleOperationRegistry.CatalogVersion + 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        ModuleWorkExecutionContext work = Work(stale, fixture.Operation);

        QuotaSystemAuthority.IsAllowed(work, fixture.Catalog, OrganizationId, AuditActorId,
            OwnerModuleId, ResourceKey).ShouldBeFalse();
    }

    [TestMethod]
    [DataRow("expected-organization")]
    [DataRow("expected-actor")]
    [DataRow("effective-actor")]
    [DataRow("missing-origin-actor")]
    public void OrganizationAndActorIdentityMustMatchTheCurrentWork(string mutation)
    {
        AuthorityFixture fixture = CreateFixture(allowedQuotaResources: [ResourceKey]);
        Guid expectedOrganization = mutation == "expected-organization" ? Guid.NewGuid() : OrganizationId;
        Guid expectedActor = mutation == "expected-actor" ? Guid.NewGuid() : AuditActorId;
        ModuleExecutionIdentity identity = mutation switch
        {
            "effective-actor" => fixture.Identity with { EffectiveActorId = OriginActorId },
            "missing-origin-actor" => fixture.Identity with { OriginActorId = null },
            _ => fixture.Identity
        };
        ModuleWorkExecutionContext work = Work(identity, fixture.Operation);

        QuotaSystemAuthority.IsAllowed(work, fixture.Catalog, expectedOrganization, expectedActor,
            OwnerModuleId, ResourceKey).ShouldBeFalse();
    }

    [TestMethod]
    [DataRow("owner")]
    [DataRow("resource")]
    public void OwnerAndQuotaResourceMustMatchTheInstalledGrant(string mutation)
    {
        AuthorityFixture fixture = CreateFixture(allowedQuotaResources: [ResourceKey]);
        string owner = mutation == "owner" ? "projects" : OwnerModuleId;
        string resource = mutation == "resource" ? "documents.other" : ResourceKey;

        QuotaSystemAuthority.IsAllowed(fixture.Work, fixture.Catalog, OrganizationId, AuditActorId,
            owner, resource).ShouldBeFalse();
    }

    [TestMethod]
    public void ClonedOperationDescriptorIsNotCurrentCatalogProvenance()
    {
        AuthorityFixture fixture = CreateFixture(allowedQuotaResources: [ResourceKey]);
        ModuleWorkExecutionContext work = Work(fixture.Identity, fixture.Operation with { });

        QuotaSystemAuthority.IsAllowed(work, fixture.Catalog, OrganizationId, AuditActorId,
            OwnerModuleId, ResourceKey).ShouldBeFalse();
    }

    private static AuthorityFixture CreateFixture(IReadOnlyList<string> allowedQuotaResources)
    {
        DataResourceDescriptor businessResource = new(
            BusinessResourceKey, "app", "documents", ModuleDataOwnership.Organization,
            IsolationPolicy: "documents-organization-isolation");
        ModuleOperationDescriptor operation = new(
            OwnerModuleId, OperationKey, 1, ModuleExecutionKind.ScheduledSystem,
            "documents.inventory-payload", 1, [], BusinessResourceKey, GrantKey, 1024);
        ModuleSystemGrantDescriptor grant = new(
            GrantKey, 1, OwnerModuleId, AuditActorId, [OperationKey], [BusinessResourceKey])
        {
            AllowedQuotaResources = allowedQuotaResources
        };
        ModuleDescriptor descriptor = new(
            OwnerModuleId, "Documents", "1.2.0", "Quota authority tests.", [], [],
            ModuleCapabilities.Api | ModuleCapabilities.BackgroundWork | ModuleCapabilities.Data, [])
        {
            DefaultDataOwnership = ModuleDataOwnership.Organization,
            QuotaResources =
            [
                new(ResourceKey, "Document storage", "Logical uploaded bytes.", ModuleQuotaUnit.Bytes)
            ],
            DataResources = [businessResource],
            Operations = [operation],
            SystemGrants = [grant]
        };
        ModuleCatalog catalog = new([new StubModule(descriptor)]);

        ModuleExecutionIdentity identity = new(
            OrganizationId, AuditActorId, OriginActorId, null, ModuleExecutionKind.ScheduledSystem,
            GrantKey, 1, null, ModuleOperationRegistry.CatalogVersion, null);
        return new(catalog, operation, grant, identity, Work(identity, operation));
    }

    private static ModuleWorkExecutionContext Work(ModuleExecutionIdentity identity, ModuleOperationDescriptor operation)
    {
        ModuleWorkExecutionContext context = new();
        context.Initialize(identity, operation);
        return context;
    }

    private sealed record AuthorityFixture(
        ModuleCatalog Catalog,
        ModuleOperationDescriptor Operation,
        ModuleSystemGrantDescriptor Grant,
        ModuleExecutionIdentity Identity,
        ModuleWorkExecutionContext Work);

    private sealed class StubModule(ModuleDescriptor descriptor) : IModule
    {
        public ModuleDescriptor Descriptor { get; } = descriptor;
        public void Register(IServiceCollection services, IConfiguration configuration) { }
    }
}
