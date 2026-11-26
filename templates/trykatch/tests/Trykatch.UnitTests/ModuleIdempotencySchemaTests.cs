using Microsoft.EntityFrameworkCore;
using Shouldly;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class ModuleIdempotencySchemaTests
{
    [TestMethod]
    public void ReceiptIsHostOwnedAndOrganizationScopedWithoutBusinessModules()
    {
        using ApplicationDbContext context = new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=never-open;Username=none;Password=none").Options);
        var entity = context.Model.GetEntityTypes().SingleOrDefault(candidate =>
            candidate.GetSchema() == "app" && candidate.GetTableName() == "mutation_receipts");

        entity.ShouldNotBeNull();
        entity.FindPrimaryKey()!.Properties.Select(property => property.Name).ShouldBe(["OrganizationId", "ActorId", "ModuleId", "OperationId", "KeyHash"]);
        entity.FindProperty("KeyHash")!.GetMaxLength().ShouldBe(64);
        entity.GetForeignKeys().ShouldBeEmpty();
        entity.GetDeclaredQueryFilters().ShouldNotBeEmpty();
        typeof(IOrganizationOwned).IsAssignableFrom(entity.ClrType).ShouldBeTrue();
    }

    [TestMethod]
    public void ReceiptHasExactOrganizationReadAppendUpdateGrantsAndNoDelete()
    {
        const string relation = "app.mutation_receipts";
        Dictionary<string, DataResourceDescriptor> resources = OperationalDataResources.All
            .ToDictionary(resource => $"{resource.Schema}.{resource.Table}", StringComparer.Ordinal);
        RuntimeDatabaseAccessProfiles.HostOwnedRelations.ShouldContain(relation);
        resources.ShouldContainKey(relation);
        DataResourceDescriptor resource = resources[relation];
        resource.Ownership.ShouldBe(ModuleDataOwnership.Organization);
        resource.AccessRule.ShouldBe(ModuleDataAccessRule.HostOnly);
        ModuleDataResourceRules.Validate(resource, hostOwned: true);
        Should.Throw<InvalidOperationException>(() => ModuleDataResourceRules.Validate(resource));

        foreach (var role in Enum.GetValues<RuntimeDatabaseRoleKind>())
        {
            string[] expected = role == RuntimeDatabaseRoleKind.Organization ? ["INSERT", "SELECT", "UPDATE"] : [];
            RuntimeDatabaseAccessProfiles.PermissionsFor(role, relation, resources).Order().ShouldBe(expected);
            RuntimeDatabaseAccessProfiles.PermissionsFor(role, relation, new Dictionary<string, DataResourceDescriptor>())
                .ShouldBeEmpty();
            RuntimeDatabaseAccessProfiles.PermissionsFor(role, relation, new Dictionary<string, DataResourceDescriptor>
            {
                [relation] = resource with { AccessRule = null }
            }).ShouldBeEmpty();
        }
    }
}
