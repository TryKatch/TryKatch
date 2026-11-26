using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class QuotaUploadPersistenceSchemaTests
{
    private const string QueueType = "Trykatch.Infrastructure.Quotas.QuotaMaintenanceWorkRecord";
    private const string UploadType = "Trykatch.Infrastructure.Quotas.ModuleUploadOperationRecord";
    private const string Relation = "infrastructure.quota_maintenance_work";
    private const string UnusedConnection = "Host=127.0.0.1;Port=1;Database=never-open;Username=none;Password=none";
    private static readonly string[] QueueColumns =
    [
        "OrganizationId", "Id", "Kind", "ResourceKey", "ReservationId", "UploadOperationId",
        "State", "DueAtUtc", "CreatedAtUtc", "Attempts", "LeaseId", "LeaseUntilUtc", "LastFailureCode"
    ];
    private static readonly string[] QueuePrimaryKey = ["OrganizationId", "Id"];
    private static readonly string[] NoPermissions = [];

    [TestMethod]
    public void QueueIsHostOwnedWithClosedOperationalSchemaAndTenantFirstKey()
    {
        using ApplicationDbContext db = CreateApplicationContext();
        IEntityType? queue = db.Model.FindEntityType(QueueType);

        queue.ShouldNotBeNull("The infrastructure quota maintenance queue must be part of the host model.");
        queue!.GetSchema().ShouldBe("infrastructure");
        queue.GetTableName().ShouldBe("quota_maintenance_work");
        typeof(IOrganizationOwned).IsAssignableFrom(queue.ClrType).ShouldBeFalse();
        queue.GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal)
            .ShouldBe(QueueColumns.Order(StringComparer.Ordinal));
        queue.FindPrimaryKey()!.Properties.Select(property => property.Name).ShouldBe(QueuePrimaryKey);
        queue.FindProperty("ResourceKey")!.GetMaxLength().ShouldBe(120);
        queue.FindProperty("LastFailureCode")!.GetMaxLength().ShouldBe(80);
        queue.GetForeignKeys().Any(foreignKey =>
            foreignKey.Properties.Select(property => property.Name).SequenceEqual(["OrganizationId", "ResourceKey"])
            && foreignKey.DeleteBehavior == DeleteBehavior.Restrict).ShouldBeTrue();
        queue.GetForeignKeys().Any(foreignKey =>
            foreignKey.Properties.Select(property => property.Name).SequenceEqual(["OrganizationId", "ReservationId"])
            && foreignKey.DeleteBehavior == DeleteBehavior.Restrict).ShouldBeTrue();
        queue.GetForeignKeys().Any(foreignKey =>
            foreignKey.Properties.Select(property => property.Name).SequenceEqual(["OrganizationId", "ResourceKey", "UploadOperationId"])
            && foreignKey.DeleteBehavior == DeleteBehavior.Restrict).ShouldBeTrue();
        queue.GetIndexes().Any(index => index.IsUnique
            && index.Properties.Select(property => property.Name)
                .SequenceEqual(["OrganizationId", "Kind", "ResourceKey", "ReservationId"])
            && index.GetFilter() is not null).ShouldBeTrue();
        queue.GetIndexes().Any(index => index.IsUnique
            && index.Properties.Select(property => property.Name).SequenceEqual(["OrganizationId", "ResourceKey"])
            && index.GetFilter() is not null).ShouldBeTrue();

        RuntimeDatabaseAccessProfiles.HostOwnedRelations.ShouldContain(Relation);
        Dictionary<string, DataResourceDescriptor> resources = OperationalDataResources.All
            .ToDictionary(resource => $"{resource.Schema}.{resource.Table}", StringComparer.Ordinal);
        resources.ShouldContainKey(Relation);
        resources[Relation].Ownership.ShouldBe(ModuleDataOwnership.Infrastructure);
        resources[Relation].AccessRule.ShouldBe(ModuleDataAccessRule.HostOnly);
        resources[Relation].EntityType.ShouldBe(queue.ClrType.FullName);
        foreach (RuntimeDatabaseRoleKind role in Enum.GetValues<RuntimeDatabaseRoleKind>())
        {
            string[] expected = role switch
            {
                RuntimeDatabaseRoleKind.Organization => ["INSERT"],
                RuntimeDatabaseRoleKind.WorkCoordinator => ["SELECT", "UPDATE"],
                _ => NoPermissions
            };
            RuntimeDatabaseAccessProfiles.PermissionsFor(role, Relation, resources)
                .Order(StringComparer.Ordinal).ToArray().ShouldBe(expected);
        }
    }

    [TestMethod]
    public void QueueAndUploadPolicyChecksArePresentInTheDesignModel()
    {
        using ApplicationDbContext db = CreateApplicationContext();
        IEntityType designQueue = db.GetService<IDesignTimeModel>().Model.FindEntityType(QueueType)!;
        Dictionary<string, string> queueChecks = designQueue.GetCheckConstraints()
            .ToDictionary(constraint => constraint.Name!, constraint => constraint.Sql!, StringComparer.Ordinal);
        queueChecks.Count.ShouldBeGreaterThanOrEqualTo(4);
        string queueSql = string.Join("\n", queueChecks.Values);
        queueSql.ShouldContain("\"Kind\" IN (1, 2, 3)");
        queueSql.ShouldContain("\"State\" IN (0, 1, 2, 3)");
        queueSql.ShouldContain("isfinite(\"DueAtUtc\")");
        queueSql.ShouldContain("isfinite(\"CreatedAtUtc\")");
        queueSql.ShouldContain("\"Attempts\" >= 0");

        IEntityType designUpload = db.GetService<IDesignTimeModel>().Model.FindEntityType(UploadType)!;
        designUpload.FindProperty("UploadPolicyKey").ShouldNotBeNull();
        designUpload.FindProperty("UploadPolicyKey")!.IsNullable.ShouldBeTrue();
        designUpload.FindProperty("UploadPolicyKey")!.GetMaxLength().ShouldBe(120);
        designUpload.FindProperty("UploadPolicyVersion")!.IsNullable.ShouldBeTrue();
        designUpload.FindProperty("UploadPolicyVersion")!.ClrType.ShouldBe(typeof(int?));
        Dictionary<string, string> uploadChecks = designUpload.GetCheckConstraints()
            .ToDictionary(constraint => constraint.Name!, constraint => constraint.Sql!, StringComparer.Ordinal);
        string uploadSql = string.Join("\n", uploadChecks.Values);
        uploadSql.ShouldContain("UploadPolicyKey");
        uploadSql.ShouldContain("UploadPolicyVersion");
    }

    [TestMethod]
    public void QueueDoesNotStoreActorsPayloadOrAuthorizationState()
    {
        using ApplicationDbContext db = CreateApplicationContext();
        IEntityType queue = db.Model.FindEntityType(QueueType)!;

        queue.GetProperties().Select(property => property.Name).ShouldNotContain(name =>
            name.Contains("Actor", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Payload", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Permission", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Handler", StringComparison.OrdinalIgnoreCase));
    }

    private static ApplicationDbContext CreateApplicationContext() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseNpgsql(UnusedConnection).Options);
}
