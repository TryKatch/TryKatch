using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class OrganizationQuotaSchemaTests
{
    [TestMethod]
    [DataRow("organization_entitlements", "OrganizationEntitlementRecord")]
    [DataRow("organization_quota_reservations", "OrganizationQuotaReservationRecord")]
    [DataRow("organization_usage", "OrganizationUsageRecord")]
    [DataRow("organization_quota_inventory", "OrganizationQuotaInventoryRecord")]
    [DataRow("module_upload_operations", "ModuleUploadOperationRecord")]
    [DataRow("organization_request_limits", "OrganizationRequestLimitRecord")]
    public void QuotaRelationsUseTheExistingOrganizationModelBoundary(string table, string typeName)
    {
        using ApplicationDbContext db = new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=design_only;Username=design_only;Password=design_only").Options);

        IEntityType? entity = db.Model.FindEntityType($"Trykatch.Infrastructure.Quotas.{typeName}");

        entity.ShouldNotBeNull($"Expected app.{table} in the host application model.");
        entity!.GetSchema().ShouldBe("app");
        entity.GetTableName().ShouldBe(table);
        typeof(IOrganizationOwned).IsAssignableFrom(entity.ClrType).ShouldBeTrue();
        entity.FindPrimaryKey()!.Properties[0].Name.ShouldBe(nameof(IOrganizationOwned.OrganizationId));
        entity.FindDeclaredQueryFilter(ApplicationModelIsolationValidator.OrganizationIsolationFilter).ShouldNotBeNull();
    }

    [TestMethod]
    [DataRow("organization_entitlements")]
    [DataRow("organization_quota_reservations")]
    [DataRow("organization_usage")]
    [DataRow("organization_quota_inventory")]
    [DataRow("module_upload_operations")]
    [DataRow("organization_request_limits")]
    public void QuotaRelationsGrantOnlyTheirExactExistingProfiles(string table)
    {
        string relation = $"app.{table}";
        Dictionary<string, DataResourceDescriptor> resources = OperationalDataResources.All
            .ToDictionary(resource => $"{resource.Schema}.{resource.Table}", StringComparer.Ordinal);
        resources.ShouldContainKey(relation);
        resources[relation].AccessRule.ShouldBe(ModuleDataAccessRule.HostOnly);

        foreach (RuntimeDatabaseRoleKind role in Enum.GetValues<RuntimeDatabaseRoleKind>())
        {
            string[] expected = (table, role) switch
            {
                ("organization_entitlements", RuntimeDatabaseRoleKind.Organization) => ["SELECT"],
                ("organization_entitlements", RuntimeDatabaseRoleKind.Platform) => ["INSERT", "SELECT", "UPDATE"],
                ("organization_usage", RuntimeDatabaseRoleKind.Organization) => ["INSERT", "SELECT"],
                (_, RuntimeDatabaseRoleKind.Organization) => ["INSERT", "SELECT", "UPDATE"],
                _ => []
            };
            RuntimeDatabaseAccessProfiles.PermissionsFor(role, relation, resources)
                .Order(StringComparer.Ordinal).ToArray().ShouldBe(expected);
        }
    }
}
