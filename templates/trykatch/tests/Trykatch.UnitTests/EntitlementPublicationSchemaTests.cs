using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class EntitlementPublicationSchemaTests
{
    private const string Relation = "platform.entitlement_publication_history";
    private const string UnusedConnection = "Host=127.0.0.1;Port=1;Database=never-open;Username=none;Password=none";
    private static readonly string[] PublicationColumns =
        ["ActorId", "InitialResourceCount", "OrganizationId", "PublishedAtUtc", "Source", "SourceHash", "SourceRevision", "SourceVersion"];
    private static readonly string[] PublicationKey = ["OrganizationId", "SourceRevision"];
    private static readonly string[] PolicyNames =
        ["entitlement_publication_history_platform_insert", "entitlement_publication_history_platform_read"];
    private static readonly string[] PlatformRole = ["trykatch_platform_runtime"];
    private static readonly string[] OrganizationForeignKey = ["OrganizationId"];
    private static readonly string[] PlatformPermissions = ["INSERT", "SELECT"];
    private static readonly string[] NoPermissions = [];

    [TestMethod]
    public void PublicationHeaderIsMappedOnlyInThePlatformModel()
    {
        using PlatformDbContext platform = CreatePlatformContext();
        using ApplicationDbContext application = new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(UnusedConnection).Options);

        IEntityType? entity = platform.Model.FindEntityType(
            "Trykatch.Infrastructure.Quotas.EntitlementPublicationHistory");

        entity.ShouldNotBeNull();
        entity!.GetSchema().ShouldBe("platform");
        entity.GetTableName().ShouldBe("entitlement_publication_history");
        entity.GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal)
            .ShouldBe(PublicationColumns.Order(StringComparer.Ordinal));
        entity.FindPrimaryKey()!.Properties.Select(property => property.Name)
            .ShouldBe(PublicationKey);
        entity.GetForeignKeys().ShouldContain(foreignKey =>
            foreignKey.Properties.Select(property => property.Name).SequenceEqual(OrganizationForeignKey)
            && foreignKey.PrincipalEntityType.ClrType.FullName == "Trykatch.Domain.Organizations.Organization"
            && foreignKey.DeleteBehavior == DeleteBehavior.Restrict);

        entity.FindProperty("OrganizationId")!.ClrType.ShouldBe(typeof(Guid));
        entity.FindProperty("SourceRevision")!.ClrType.ShouldBe(typeof(long));
        entity.FindProperty("SourceHash")!.GetMaxLength().ShouldBe(64);
        entity.FindProperty("Source")!.GetMaxLength().ShouldNotBeNull();
        entity.FindProperty("ActorId")!.ClrType.ShouldBe(typeof(Guid));
        entity.FindProperty("InitialResourceCount")!.ClrType.ShouldBe(typeof(int));
        entity.FindProperty("PublishedAtUtc")!.ClrType.ShouldBe(typeof(DateTimeOffset));
        entity.FindProperty("SourceVersion")!.ClrType.ShouldBe(typeof(int));

        IEntityType designTimeEntity = platform.GetService<IDesignTimeModel>().Model.FindEntityType(
            "Trykatch.Infrastructure.Quotas.EntitlementPublicationHistory")!;
        Dictionary<string, string> checks = designTimeEntity.GetCheckConstraints()
            .ToDictionary(constraint => constraint.Name!, constraint => constraint.Sql!, StringComparer.Ordinal);
        checks.ShouldContainKey("CK_entitlement_publication_history_identity");
        checks.ShouldContainKey("CK_entitlement_publication_history_source");
        string identityCheck = checks["CK_entitlement_publication_history_identity"];
        identityCheck.ShouldContain("\"OrganizationId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
        identityCheck.ShouldContain("\"SourceRevision\" >= 0");
        identityCheck.ShouldContain("\"ActorId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
        identityCheck.ShouldContain("\"InitialResourceCount\" BETWEEN 0 AND 10000");
        identityCheck.ShouldContain("isfinite(\"PublishedAtUtc\")");
        string sourceCheck = checks["CK_entitlement_publication_history_source"];
        sourceCheck.ShouldContain("\"SourceHash\" ~ '^[A-Fa-f0-9]{64}$'");
        sourceCheck.ShouldContain("\"Source\" = 'host-configuration'");
        sourceCheck.ShouldContain("\"SourceVersion\" = 1");
        platform.Model.GetEntityTypes().Where(candidate => candidate.GetSchema() == "app")
            .ShouldBeEmpty("The platform model must not map application business entities.");
        application.Model.GetEntityTypes().ShouldNotContain(candidate =>
            candidate.GetSchema() == "platform" && candidate.GetTableName() == "entitlement_publication_history");
    }

    [TestMethod]
    public void PublicationHistoryResourceAndRuntimePrivilegesAreExact()
    {
        Dictionary<string, DataResourceDescriptor> resources = OperationalDataResources.All
            .ToDictionary(resource => $"{resource.Schema}.{resource.Table}", StringComparer.Ordinal);

        RuntimeDatabaseAccessProfiles.HostOwnedRelations.ShouldContain(Relation);
        resources.ShouldContainKey(Relation);
        DataResourceDescriptor resource = resources[Relation];
        resource.Ownership.ShouldBe(ModuleDataOwnership.Platform);
        resource.AccessRule.ShouldBe(ModuleDataAccessRule.PlatformOnly);
        resource.IsolationPolicy.ShouldBe("entitlement_publication_history_platform_read");
        resource.EntityType.ShouldBeNull();
        ModuleDataResourceRules.Validate(resource, hostOwned: true);

        foreach (RuntimeDatabaseRoleKind role in Enum.GetValues<RuntimeDatabaseRoleKind>())
        {
            string[] expected = role == RuntimeDatabaseRoleKind.Platform ? PlatformPermissions : NoPermissions;
            RuntimeDatabaseAccessProfiles.PermissionsFor(role, Relation, resources)
                .Order(StringComparer.Ordinal).ToArray().ShouldBe(expected);
            RuntimeDatabaseAccessProfiles.PermissionsFor(role, Relation, new Dictionary<string, DataResourceDescriptor>())
                .ShouldBeEmpty();
            RuntimeDatabaseAccessProfiles.PermissionsFor(role, Relation, new Dictionary<string, DataResourceDescriptor>
            {
                [Relation] = resource with { AccessRule = null }
            }).ShouldBeEmpty();
        }
    }

    [TestMethod]
    public void PublicationHistoryPoliciesConstrainReadsAndAppendsToTheTargetOrganization()
    {
        HostPostgresPolicyContracts.All.ShouldContainKey(Relation);
        HostPostgresPolicyContracts.Policy[] policies = HostPostgresPolicyContracts.All[Relation];
        policies.Select(policy => policy.Name).Order(StringComparer.Ordinal).ShouldBe(PolicyNames);

        const string organization = "(\"OrganizationId\" = (NULLIF(current_setting('app.organization_id'::text, true), ''::text))::uuid)";
        const string nonemptyActor = "((NULLIF(current_setting('app.actor_id'::text, true), ''::text))::uuid <> '00000000-0000-0000-0000-000000000000'::uuid)";
        const string actorRow = "(\"ActorId\" = (NULLIF(current_setting('app.actor_id'::text, true), ''::text))::uuid)";
        const string platformProfile = "(CURRENT_USER = 'trykatch_platform_runtime'::name)";
        string expectedPredicate = $"({organization} AND {nonemptyActor} AND {platformProfile})";

        HostPostgresPolicyContracts.Policy read = policies.Single(policy => policy.Command == "r");
        read.Using.ShouldBe(expectedPredicate);
        read.WithCheck.ShouldBeEmpty();
        read.ExpectedRoles.ShouldBe(PlatformRole);

        HostPostgresPolicyContracts.Policy insert = policies.Single(policy => policy.Command == "a");
        insert.Using.ShouldBeEmpty();
        insert.WithCheck.ShouldBe($"({organization} AND {nonemptyActor} AND {platformProfile} AND {actorRow})");
        insert.ExpectedRoles.ShouldBe(PlatformRole);
        policies.ShouldNotContain(policy => policy.Command == "w" || policy.Command == "d" || policy.Command == "*");
    }

    private static PlatformDbContext CreatePlatformContext() => new(new DbContextOptionsBuilder<PlatformDbContext>()
        .UseNpgsql(UnusedConnection).Options);
}
