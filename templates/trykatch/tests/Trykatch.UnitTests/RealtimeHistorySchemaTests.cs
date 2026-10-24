using Microsoft.EntityFrameworkCore;
using Shouldly;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class RealtimeHistorySchemaTests
{
    private static readonly string[] HistoryIdentity = ["OrganizationId", "ModuleId", "EventId"];
    [TestMethod]
    [DataRow("realtime_streams")]
    [DataRow("realtime_history")]
    public void HistoryAndCursorAreOrganizationScopedHostResources(string table)
    {
        using ApplicationDbContext context = new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=never-open;Username=none;Password=none").Options);
        var entity = context.Model.GetEntityTypes().SingleOrDefault(candidate =>
            candidate.GetSchema() == "app" && candidate.GetTableName() == table);
        entity.ShouldNotBeNull();
        entity.GetDeclaredQueryFilters().ShouldNotBeEmpty();
        typeof(IOrganizationOwned).IsAssignableFrom(entity.ClrType).ShouldBeTrue();
        entity.GetForeignKeys().ShouldBeEmpty();
        if (table == "realtime_streams")
        {
            entity.FindPrimaryKey()!.Properties.Select(property => property.Name).ShouldBe(["OrganizationId"]);
            entity.FindProperty("Sequence")!.ClrType.ShouldBe(typeof(long));
        }
        else
        {
            entity.FindPrimaryKey()!.Properties.Select(property => property.Name).ShouldBe(["OrganizationId", "Sequence"]);
            entity.GetIndexes().ShouldContain(index => index.IsUnique && index.Properties.Select(property => property.Name)
                .SequenceEqual(HistoryIdentity));
            entity.FindProperty("ModuleId")!.GetMaxLength().ShouldBe(80);
            entity.FindProperty("EventKey")!.GetMaxLength().ShouldBe(160);
            entity.FindProperty("PolicyKey")!.GetMaxLength().ShouldBe(160);
            entity.FindProperty("ResourceKey")!.GetMaxLength().ShouldBe(160);
        }
    }

    [TestMethod]
    [DataRow("realtime_streams")]
    [DataRow("realtime_history")]
    public void RuntimeGrantsCannotMutateOrDeleteCommittedHistory(string table)
    {
        string relation = "app." + table;
        Dictionary<string, DataResourceDescriptor> resources = OperationalDataResources.All
            .ToDictionary(resource => $"{resource.Schema}.{resource.Table}", StringComparer.Ordinal);
        RuntimeDatabaseAccessProfiles.HostOwnedRelations.ShouldContain(relation);
        resources.ShouldContainKey(relation);
        DataResourceDescriptor resource = resources[relation];
        resource.Ownership.ShouldBe(ModuleDataOwnership.Organization);
        resource.AccessRule.ShouldBe(ModuleDataAccessRule.HostOnly);
        ModuleDataResourceRules.Validate(resource, hostOwned: true);
        Should.Throw<InvalidOperationException>(() => ModuleDataResourceRules.Validate(resource));
        foreach (RuntimeDatabaseRoleKind role in Enum.GetValues<RuntimeDatabaseRoleKind>())
        {
            string[] expected = role != RuntimeDatabaseRoleKind.Organization ? []
                : table == "realtime_streams" ? ["INSERT", "SELECT", "UPDATE"] : ["INSERT", "SELECT"];
            RuntimeDatabaseAccessProfiles.PermissionsFor(role, relation, resources).Order().ShouldBe(expected);
            RuntimeDatabaseAccessProfiles.PermissionsFor(role, relation, new Dictionary<string, DataResourceDescriptor>()).ShouldBeEmpty();
            RuntimeDatabaseAccessProfiles.PermissionsFor(role, relation, new Dictionary<string, DataResourceDescriptor>
            { [relation] = resource with { AccessRule = null } }).ShouldBeEmpty();
        }
    }
}
