using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Shouldly;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Domain.Organizations;
using Trykatch.Modules;
using Trykatch.Modules.Documents.Infrastructure;
using Trykatch.Modules.Projects.Infrastructure;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class ModuleCompositionMigrationTests
{
    private const string Unavailable = "Host=127.0.0.1;Port=1;Database=never-open;Username=none;Password=none";

    [TestMethod]
    [DataRow(true, true)]
    [DataRow(true, false)]
    [DataRow(false, false)]
    public void SqlOwnedModuleCompositionDoesNotRequireHostMigrations(bool projects, bool documents)
    {
        using ApplicationDbContext context = CreateContext(projects, documents);
        context.Database.HasPendingModelChanges().ShouldBeFalse();
    }

    [TestMethod]
    public void ProjectsSqlOwnsItsTableWhileHostTablesRemainEfManaged()
    {
        using ApplicationDbContext context = CreateContext(true, true);
        var model = context.GetService<IDesignTimeModel>().Model;
        model.GetEntityTypes().Single(entity => entity.GetTableName() == "projects")
            .IsTableExcludedFromMigrations().ShouldBeTrue();
        model.GetEntityTypes().Single(entity => entity.GetTableName() == "audit_entries")
            .IsTableExcludedFromMigrations().ShouldBeFalse();
        model.GetEntityTypes().Single(entity => entity.GetTableName() == "realtime_history")
            .IsTableExcludedFromMigrations().ShouldBeFalse();
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void UnrelatedHostModelDriftStillRequiresMigration(bool projects)
    {
        using ApplicationDbContext context = CreateContext(projects, false, drift: true);
        context.Database.HasPendingModelChanges().ShouldBeTrue();
    }

    [TestMethod]
    public void OwnershipCheckpointHasNoSchemaOperations()
    {
        using ApplicationDbContext context = CreateContext(true, true);
        var assembly = context.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(assembly.Migrations.Single(pair =>
            pair.Key.EndsWith("_RespectProjectsSqlMigrationOwnership", StringComparison.Ordinal)).Value,
            context.Database.ProviderName!);
        migration.UpOperations.ShouldBeEmpty();
        migration.DownOperations.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task ExistingDataAndSqlLedgerSurviveDisabledUpgradeAndReenable()
    {
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder(
            "postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f").Build();
        await postgres.StartAsync();
        string connection = postgres.GetConnectionString();
        await PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(connection);
        var initial = await ModuleJobMigrationIntegrationTests.InvokeMigratorAsync(connection);
        initial.Exit.ShouldBe(0, initial.Error + initial.Output);

        // The checkpoint has empty Down operations: this restores the exact pre-checkpoint
        // EF ledger while retaining the existing production schema and module declarations.
        await using (ApplicationDbContext enabled = CreateContext(true, true, connection: connection))
            await enabled.GetService<IMigrator>().MigrateAsync("20261024064323_AddOrganizationRealtimeHistory");

        await using NpgsqlConnection owner = new(connection);
        await owner.OpenAsync();
        Guid organization = Guid.NewGuid(), project = Guid.NewGuid();
        await using (NpgsqlCommand seed = new("""
            INSERT INTO app.projects ("Id", "OrganizationId", "Name", "Description", "CreatedBy", "CreatedAt")
            VALUES (@project, @organization, 'Retained project', 'Retained description', @actor, CURRENT_TIMESTAMP);
            INSERT INTO app.realtime_streams ("OrganizationId", "Sequence") VALUES (@organization, 1);
            INSERT INTO app.realtime_history ("OrganizationId", "Sequence", "ModuleId", "EventKey", "SchemaVersion", "EventId",
                "PolicyOwnerModuleId", "PolicyKey", "PolicyVersion", "AudienceKind", "SourceMessageId", "CreatedAtUtc")
            VALUES (@organization, 1, 'projects', 'projects.created', 1, @event,
                'projects', 'projects.realtime-read', 1, 0, @source, CURRENT_TIMESTAMP);
            """, owner))
        {
            seed.Parameters.AddWithValue("project", project);
            seed.Parameters.AddWithValue("organization", organization);
            seed.Parameters.AddWithValue("actor", Guid.NewGuid());
            seed.Parameters.AddWithValue("event", Guid.NewGuid());
            seed.Parameters.AddWithValue("source", Guid.NewGuid());
            await seed.ExecuteNonQueryAsync();
        }
        string before = await ReadRetainedStateAsync(owner);
        await using (ApplicationDbContext disabled = CreateContext(false, false, connection: connection))
        {
            await disabled.Database.MigrateAsync();
            await disabled.Database.MigrateAsync();
            disabled.Database.HasPendingModelChanges().ShouldBeFalse();
        }
        (await ReadRetainedStateAsync(owner)).ShouldBe(before);
        var reenabled = await ModuleJobMigrationIntegrationTests.InvokeMigratorAsync(connection);
        reenabled.Exit.ShouldBe(0, reenabled.Error + reenabled.Output);
        (await ReadRetainedStateAsync(owner)).ShouldBe(before);
    }

    private static async Task<string> ReadRetainedStateAsync(NpgsqlConnection connection)
    {
        await using NpgsqlCommand read = new("""
            SELECT jsonb_build_array(
                (SELECT jsonb_agg(to_jsonb(p) ORDER BY "Id") FROM app.projects p),
                (SELECT jsonb_agg(to_jsonb(h) ORDER BY "OrganizationId", "Sequence") FROM app.realtime_history h),
                (SELECT jsonb_agg(to_jsonb(m) ORDER BY module_id, migration_id) FROM platform.module_migrations m),
                (SELECT jsonb_agg(to_jsonb(r) ORDER BY schema_name, table_name) FROM platform.module_data_resources r))::text;
            """, connection);
        return (string)(await read.ExecuteScalarAsync())!;
    }

    private static ApplicationDbContext CreateContext(bool projects, bool documents, bool drift = false, string connection = Unavailable)
    {
        List<IApplicationModelContributor> contributors = [];
        List<IModule> modules = [];
        if (projects) { contributors.Add(new ProjectsModelContributor()); modules.Add(new ProjectsModule()); }
        if (documents) { contributors.Add(new DocumentsModelContributor()); modules.Add(new DocumentsModule()); }
        if (drift) contributors.Add(new HostDriftContributor());
        return new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connection).Options,
            contributors, moduleCatalog: new ModuleCatalog(modules));
    }

    private sealed class HostDriftContributor : IApplicationModelContributor
    {
        public string ModuleId => "host-drift-regression";
        public void Configure(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<AuditEntry>().Property<string>("UnmigratedHostProperty");
    }
}
