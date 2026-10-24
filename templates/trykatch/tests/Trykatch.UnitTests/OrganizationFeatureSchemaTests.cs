using Microsoft.EntityFrameworkCore;
using Shouldly;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class OrganizationFeatureSchemaTests
{
    private static readonly string[] OrganizationForeignKey = ["OrganizationId"];
    private static readonly string[] HistoryIndex = ["Environment", "FeatureKey", "OrganizationId", "ChangedAtUtc", "Id"];
    private static readonly (string Table, string[] Columns, string[] PrimaryKey)[] Relations =
    [
        ("feature_environment_controls",
            ["Environment", "FeatureKey", "DefaultEnabled", "ForceDisabled", "Version", "UpdatedAtUtc"],
            ["Environment", "FeatureKey"]),
        ("organization_feature_overrides",
            ["OrganizationId", "Environment", "FeatureKey", "Enabled", "Version", "UpdatedAtUtc"],
            ["OrganizationId", "Environment", "FeatureKey"]),
        ("feature_control_history",
            ["Id", "Environment", "FeatureKey", "OrganizationId", "ActorId", "PreviousVersion", "Version",
                "PreviousEnabled", "Enabled", "PreviousForceDisabled", "ForceDisabled", "ChangedAtUtc"],
            ["Id"])
    ];

    [TestMethod]
    public void FeatureControlRecordsAreMappedOnlyByPlatformAndOrganizationControlPlaneContexts()
    {
        using PlatformDbContext platform = CreatePlatformContext();
        using OrganizationControlPlaneDbContext organization = new(new DbContextOptionsBuilder<OrganizationControlPlaneDbContext>()
            .UseNpgsql(UnusedConnection).Options);
        using ApplicationDbContext application = new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(UnusedConnection).Options);

        foreach ((string table, _, _) in Relations)
        {
            platform.Model.GetEntityTypes().ShouldContain(entity => entity.GetSchema() == "platform" && entity.GetTableName() == table);
            organization.Model.GetEntityTypes().ShouldContain(entity => entity.GetSchema() == "platform" && entity.GetTableName() == table);
            application.Model.GetEntityTypes().ShouldNotContain(entity => entity.GetSchema() == "platform" && entity.GetTableName() == table);
        }
    }

    [TestMethod]
    public void FeatureControlRecordsHaveTypedBoundedColumnsAndExactKeys()
    {
        using PlatformDbContext context = CreatePlatformContext();
        var model = context.Model;

        foreach ((string table, string[] columns, string[] primaryKey) in Relations)
        {
            var entity = model.GetEntityTypes().SingleOrDefault(candidate =>
                candidate.GetSchema() == "platform" && candidate.GetTableName() == table);
            entity.ShouldNotBeNull($"Expected platform.{table} in the platform model.");
            entity!.GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal)
                .ShouldBe(columns.Order(StringComparer.Ordinal));
            entity.FindPrimaryKey()!.Properties.Select(property => property.GetColumnName()).ShouldBe(primaryKey);
            entity.GetProperties().ShouldAllBe(property => property.ClrType != typeof(System.Text.Json.JsonDocument)
                && property.GetColumnType() != "jsonb");
        }

        var environment = FindEntity(model, "feature_environment_controls");
        environment.FindProperty("Environment")!.GetMaxLength().ShouldBe(64);
        environment.FindProperty("FeatureKey")!.GetMaxLength().ShouldBe(120);
        environment.FindProperty("DefaultEnabled")!.IsNullable.ShouldBeTrue();
        environment.FindProperty("ForceDisabled")!.ClrType.ShouldBe(typeof(bool));
        environment.FindProperty("Version")!.ClrType.ShouldBe(typeof(Guid));
        environment.GetProperties().ShouldNotContain(property => property.Name.Contains("Actor", StringComparison.Ordinal)
            || property.Name.Contains("Editor", StringComparison.Ordinal));

        var organization = FindEntity(model, "organization_feature_overrides");
        organization.FindProperty("OrganizationId")!.ClrType.ShouldBe(typeof(Guid));
        organization.FindProperty("Environment")!.GetMaxLength().ShouldBe(64);
        organization.FindProperty("FeatureKey")!.GetMaxLength().ShouldBe(120);
        organization.FindProperty("Enabled")!.IsNullable.ShouldBeTrue();
        organization.FindProperty("Version")!.ClrType.ShouldBe(typeof(Guid));
        organization.GetForeignKeys().ShouldContain(foreignKey =>
            foreignKey.Properties.Select(property => property.Name).SequenceEqual(OrganizationForeignKey)
            && foreignKey.DeleteBehavior == DeleteBehavior.Restrict);

        var history = FindEntity(model, "feature_control_history");
        history.FindProperty("Id")!.ClrType.ShouldBe(typeof(Guid));
        history.FindProperty("OrganizationId")!.IsNullable.ShouldBeTrue();
        history.FindProperty("ActorId")!.ClrType.ShouldBe(typeof(Guid));
        history.FindProperty("PreviousVersion")!.IsNullable.ShouldBeTrue();
        history.FindProperty("Version")!.ClrType.ShouldBe(typeof(Guid));
        history.FindProperty("PreviousEnabled")!.IsNullable.ShouldBeTrue();
        history.FindProperty("Enabled")!.IsNullable.ShouldBeTrue();
        history.FindProperty("PreviousForceDisabled")!.IsNullable.ShouldBeTrue();
        history.FindProperty("ForceDisabled")!.IsNullable.ShouldBeTrue();
        history.GetForeignKeys().ShouldContain(foreignKey =>
            foreignKey.Properties.Select(property => property.Name).SequenceEqual(OrganizationForeignKey)
            && foreignKey.DeleteBehavior == DeleteBehavior.Restrict);
        history.GetIndexes().ShouldContain(index => index.Properties.Select(property => property.Name)
            .SequenceEqual(HistoryIndex));
    }

    [TestMethod]
    public void FeatureControlResourcesHaveExactHostOwnershipAndRuntimeGrantProfiles()
    {
        Dictionary<string, DataResourceDescriptor> resources = OperationalDataResources.All
            .ToDictionary(resource => $"{resource.Schema}.{resource.Table}", StringComparer.Ordinal);
        var expected = new Dictionary<string, (ModuleDataOwnership Ownership, ModuleDataAccessRule Rule, string? Isolation)>
        {
            ["platform.feature_environment_controls"] = (ModuleDataOwnership.Platform, ModuleDataAccessRule.PlatformOnly, null),
            ["platform.organization_feature_overrides"] = (ModuleDataOwnership.Organization, ModuleDataAccessRule.HostOnly,
                "organization_feature_overrides_read"),
            ["platform.feature_control_history"] = (ModuleDataOwnership.Platform, ModuleDataAccessRule.PlatformOnly, null)
        };

        foreach ((string relation, (ModuleDataOwnership ownership, ModuleDataAccessRule rule, string? isolation)) in expected)
        {
            RuntimeDatabaseAccessProfiles.HostOwnedRelations.ShouldContain(relation);
            resources.ShouldContainKey(relation);
            DataResourceDescriptor resource = resources[relation];
            resource.Ownership.ShouldBe(ownership);
            resource.AccessRule.ShouldBe(rule);
            resource.IsolationPolicy.ShouldBe(isolation);
            resource.EntityType.ShouldBeNull();
            ModuleDataResourceRules.Validate(resource, hostOwned: true);
            if (ownership == ModuleDataOwnership.Organization)
            {
                Should.Throw<InvalidOperationException>(() => ModuleDataResourceRules.Validate(resource));
                Should.Throw<InvalidOperationException>(() => ModuleDataResourceRules.Validate(
                    resource with { Table = "other_feature_overrides" }, hostOwned: true));
            }

            foreach (RuntimeDatabaseRoleKind role in Enum.GetValues<RuntimeDatabaseRoleKind>())
            {
                string[] permissions = RuntimeDatabaseAccessProfiles.PermissionsFor(role, relation, resources).Order().ToArray();
                string[] expectedPermissions = (role, relation) switch
                {
                    (RuntimeDatabaseRoleKind.Organization, "platform.feature_environment_controls") => ["SELECT"],
                    (RuntimeDatabaseRoleKind.Organization, "platform.organization_feature_overrides") => ["SELECT"],
                    (RuntimeDatabaseRoleKind.Platform, "platform.feature_environment_controls") => ["INSERT", "SELECT", "UPDATE"],
                    (RuntimeDatabaseRoleKind.Platform, "platform.organization_feature_overrides") => ["INSERT", "SELECT", "UPDATE"],
                    (RuntimeDatabaseRoleKind.Platform, "platform.feature_control_history") => ["INSERT", "SELECT"],
                    _ => []
                };
                permissions.ShouldBe(expectedPermissions, $"Unexpected {role} privileges on {relation}.");
                RuntimeDatabaseAccessProfiles.PermissionsFor(role, relation, new Dictionary<string, DataResourceDescriptor>
                {
                    [relation] = resource with { AccessRule = null }
                }).ShouldBeEmpty();
            }
        }
    }

    private const string UnusedConnection = "Host=127.0.0.1;Port=1;Database=never-open;Username=none;Password=none";

    private static PlatformDbContext CreatePlatformContext() => new(new DbContextOptionsBuilder<PlatformDbContext>()
        .UseNpgsql(UnusedConnection).Options);

    private static Microsoft.EntityFrameworkCore.Metadata.IEntityType FindEntity(
        Microsoft.EntityFrameworkCore.Metadata.IModel model, string table) => model.GetEntityTypes().Single(entity =>
        entity.GetSchema() == "platform" && entity.GetTableName() == table);
}
