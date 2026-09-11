using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Api.Modules;
using Trykatch.Application.Organizations;
using Trykatch.Identity;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class GeneratedOrganizationIsolationTests
{
    [TestMethod]
    public async Task TamperedHostFunctionNeverReceivesRuntimeExecuteGrants()
    {
        await using DatabaseServer server = await DatabaseServer.StartAsync();
        ModuleCatalog catalog = new(EnabledModules.All);
        ServiceCollection moduleServices = new();
        IConfiguration configuration = new ConfigurationBuilder().Build();
        foreach (IModule module in catalog.Modules)
            module.Register(moduleServices, configuration);
        await using ServiceProvider moduleProvider = moduleServices.BuildServiceProvider();
        IApplicationModelContributor[] contributors = moduleProvider.GetServices<IApplicationModelContributor>().ToArray();
        await MigrateAsync(server.ConnectionString, catalog, contributors);
        await InstalledSchemaCatalog.SynchronizeAsync(
            server.ConnectionString,
            catalog.Descriptors.SelectMany(module => module.DataResources
                .Select(resource => new InstalledDataResource(module.Id, resource))));

        await using NpgsqlConnection owner = new(server.ConnectionString);
        await owner.OpenAsync();
        await ExecuteAsync(owner, null, """
            CREATE OR REPLACE FUNCTION platform.redact_legacy_outbox_error()
            RETURNS trigger
            LANGUAGE plpgsql
            SECURITY INVOKER
            SET search_path = pg_catalog
            AS 'BEGIN RETURN NEW; END';
            """);
        RuntimeDatabaseRoles runtimeRoles = new(
            PostgresRuntimeRoleFixture.OrganizationRole,
            PostgresRuntimeRoleFixture.PlatformRole,
            PostgresRuntimeRoleFixture.IdentityRole,
            PostgresRuntimeRoleFixture.OutboxRole);
        await ExecuteAsync(owner, null, $"""
            GRANT EXECUTE ON FUNCTION platform.redact_legacy_outbox_error()
            TO {PostgresRuntimeRoleFixture.OrganizationRole}, {PostgresRuntimeRoleFixture.OutboxRole};
            """);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            RuntimeRoleProvisioner.ProvisionAsync(server.ConnectionString, runtimeRoles));

        await using NpgsqlCommand privileges = new("""
            SELECT has_function_privilege(@organization, 'platform.redact_legacy_outbox_error()', 'EXECUTE'),
                   has_function_privilege(@outbox, 'platform.redact_legacy_outbox_error()', 'EXECUTE')
            """, owner);
        privileges.Parameters.AddWithValue("organization", PostgresRuntimeRoleFixture.OrganizationRole);
        privileges.Parameters.AddWithValue("outbox", PostgresRuntimeRoleFixture.OutboxRole);
        await using NpgsqlDataReader reader = await privileges.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        reader.GetBoolean(0).ShouldBeFalse();
        reader.GetBoolean(1).ShouldBeFalse();
    }

    [TestMethod]
    public async Task EveryDeclaredOrganizationRelationIsDefaultDenyUnderTheRealRuntimeRole()
    {
        await using DatabaseServer server = await DatabaseServer.StartAsync();

        ModuleCatalog catalog = new(EnabledModules.All);
        ServiceCollection moduleServices = new();
        IConfiguration configuration = new ConfigurationBuilder().Build();
        foreach (IModule module in catalog.Modules)
            module.Register(moduleServices, configuration);
        await using ServiceProvider moduleProvider = moduleServices.BuildServiceProvider();
        IApplicationModelContributor[] contributors = moduleProvider.GetServices<IApplicationModelContributor>().ToArray();
        string ownerConnection = server.ConnectionString;
        await MigrateAsync(ownerConnection, catalog, contributors);
        await InstalledSchemaCatalog.SynchronizeAsync(
            ownerConnection,
            catalog.Descriptors.SelectMany(module => module.DataResources
                .Select(resource => new InstalledDataResource(module.Id, resource))));
        await InstalledSchemaCatalog.ValidateDeclaredObjectsAsync(
            ownerConnection,
            RuntimeDatabaseAccessProfiles.HostOwnedRelations.Concat(
                catalog.Descriptors.SelectMany(module => module.DataResources)
                    .Select(resource => $"{resource.Schema}.{resource.Table}")));
        DataResourceDescriptor[] resources = catalog.Descriptors
            .SelectMany(module => module.DataResources)
            .Where(resource => resource.Ownership == ModuleDataOwnership.Organization)
            .Append(new(
                "audit-entries", "platform", "audit_entries", ModuleDataOwnership.Organization,
                "Trykatch.Domain.Organizations.AuditEntry", "audit_organization_isolation"))
            .ToArray();

        Guid organizationA = Guid.CreateVersion7();
        Guid organizationB = Guid.CreateVersion7();
        Guid actorA = Guid.CreateVersion7();
        List<IsolationFixture> fixtures = [];
        Dictionary<(string Relation, Guid OrganizationId), FixtureRow> fixtureRows = [];
        RuntimeDatabaseRoles runtimeRoles = new(
            PostgresRuntimeRoleFixture.OrganizationRole,
            PostgresRuntimeRoleFixture.PlatformRole,
            PostgresRuntimeRoleFixture.IdentityRole,
            PostgresRuntimeRoleFixture.OutboxRole);
        await RuntimeRoleProvisioner.ProvisionAsync(ownerConnection, runtimeRoles);
        (await PostgresIsolationInspector.InspectAsync(ownerConnection, runtimeRoles, catalog.Descriptors))
            .ThrowIfInvalid();

        await using (NpgsqlConnection owner = new(ownerConnection))
        {
            await owner.OpenAsync();
            foreach (DataResourceDescriptor resource in resources)
            {
                IsolationFixture fixture = await DescribeFixtureAsync(owner, resource);
                fixtures.Add(fixture);
                Guid organizationAId = Guid.CreateVersion7();
                Guid organizationBId = Guid.CreateVersion7();
                await InsertFixtureAsync(owner, null, fixture, organizationAId, organizationA, actorA, "A");
                await InsertFixtureAsync(owner, null, fixture, organizationBId, organizationB, Guid.CreateVersion7(), "B");
                fixtureRows[(fixture.QualifiedName, organizationA)] = await ReadFixtureRowAsync(owner, fixture, organizationAId);
                fixtureRows[(fixture.QualifiedName, organizationB)] = await ReadFixtureRowAsync(owner, fixture, organizationBId);
            }
        }

        NpgsqlConnectionStringBuilder runtimeBuilder = new(ownerConnection)
        {
            Username = PostgresRuntimeRoleFixture.OrganizationRole,
            Password = PostgresRuntimeRoleFixture.OrganizationPassword
        };
        string runtimeConnection = runtimeBuilder.ConnectionString;
        await using NpgsqlConnection runtime = new(runtimeConnection);
        await runtime.OpenAsync();
        foreach (IsolationFixture fixture in fixtures)
        {
            DataResourceDescriptor resource = fixture.Resource;
            string relation = fixture.Relation;
            (await CountAsync(runtime, null, relation)).ShouldBe(0, $"{relation} must default deny without organization context");

            await using NpgsqlTransaction transaction = await runtime.BeginTransactionAsync();
            await SetContextAsync(runtime, transaction, organizationA, actorA);
            (await CountAsync(runtime, transaction, relation)).ShouldBe(1, $"{relation} must expose only organization A");
            (await ReadMarkersAsync(runtime, transaction, fixture)).ShouldBe(["A"], $"{relation} must not leak organization B rows");
            if (string.Equals(resource.Schema, "app", StringComparison.Ordinal))
            {
                (await MutateFixtureAsync(runtime, transaction, fixture, organizationB, delete: false)).ShouldBe(0,
                    $"{relation} must hide organization B from raw updates");
                (await MutateFixtureAsync(runtime, transaction, fixture, organizationB, delete: true)).ShouldBe(0,
                    $"{relation} must hide organization B from raw deletes");
                foreach (ForeignKeyFixture foreignKey in fixture.ForeignKeys)
                {
                    IsolationFixture? target = fixtures.SingleOrDefault(candidate => candidate.QualifiedName == foreignKey.TargetRelation);
                    if (target is null) continue;
                    (await CrossOrganizationAttachmentIsBlockedAsync(
                        runtime,
                        transaction,
                        fixture,
                        foreignKey,
                        fixtureRows[(fixture.QualifiedName, organizationA)].Id,
                        fixtureRows[(target.QualifiedName, organizationB)])).ShouldBeTrue(
                        $"{fixture.QualifiedName}/{foreignKey.Name} must not attach organization A rows to organization B records");
                }
                (await MutateFixtureAsync(runtime, transaction, fixture, organizationA, delete: false)).ShouldBe(1,
                    $"{relation} must allow organization A updates");
                (await MutateFixtureAsync(runtime, transaction, fixture, organizationA, delete: true)).ShouldBe(1,
                    $"{relation} must allow organization A deletes");
                await InsertFixtureAsync(runtime, transaction, fixture, Guid.CreateVersion7(), organizationA, actorA, "allowed");
                (await CountAsync(runtime, transaction, relation)).ShouldBe(1,
                    $"{relation} must allow inserts for the active organization");
            }
            await Should.ThrowAsync<NpgsqlException>(() =>
                InsertFixtureAsync(runtime, transaction, fixture, Guid.CreateVersion7(), organizationB, actorA, "blocked"));
            await transaction.RollbackAsync();
        }

        OrganizationContext scope = new();
        scope.Initialize(new(
            organizationA,
            "organization-a",
            actorA,
            Guid.CreateVersion7(),
            new HashSet<string>()));
        Should.Throw<InvalidOperationException>(() => scope.Initialize(new(
            organizationB,
            "organization-b",
            actorA,
            Guid.CreateVersion7(),
            new HashSet<string>())))
            .Message.ShouldContain("only be initialized once");
        DbContextOptions<ApplicationDbContext> runtimeOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(runtimeConnection)
            .Options;
        await using ApplicationDbContext application = new(runtimeOptions, contributors, scope, catalog);
        await using var efTransaction = await application.Database.BeginTransactionAsync();
        await application.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.organization_id', {organizationA.ToString()}, true), set_config('app.actor_id', {actorA.ToString()}, true)");
        foreach (DataResourceDescriptor resource in resources.Where(resource => resource.EntityType is not null))
        {
            Type entityType = ResolveEntityType(resource.EntityType!);
            (await CountEntityAsync(application, entityType)).ShouldBe(1,
                $"The EF model must scope declared entity '{resource.EntityType}' to organization A");
        }
    }

    [TestMethod]
    public async Task ForeignKeyTargetingUniqueNonIdKeyIsActuallyAttempted()
    {
        await using DatabaseServer server = await DatabaseServer.StartAsync();
        string suffix = Guid.NewGuid().ToString("N")[..12];
        string schema = $"fk_{suffix}";
        string role = $"fk_runtime_{suffix}";
        Guid organizationA = Guid.CreateVersion7();
        Guid organizationB = Guid.CreateVersion7();
        Guid sourceId = Guid.CreateVersion7();

        await using NpgsqlConnection owner = new(server.ConnectionString);
        await owner.OpenAsync();
        try
        {
            await ExecuteAsync(owner, null, $"""
                CREATE ROLE {role} LOGIN PASSWORD 'fk-runtime-test' NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
                CREATE SCHEMA {schema};
                CREATE TABLE {schema}.targets (
                  "Id" uuid PRIMARY KEY,
                  "OrganizationId" uuid NOT NULL,
                  "ExternalKey" text NOT NULL UNIQUE);
                CREATE TABLE {schema}.sources (
                  "Id" uuid PRIMARY KEY,
                  "OrganizationId" uuid NOT NULL,
                  "TargetExternalKey" text NOT NULL REFERENCES {schema}.targets ("ExternalKey"));
                ALTER TABLE {schema}.sources ENABLE ROW LEVEL SECURITY;
                ALTER TABLE {schema}.sources FORCE ROW LEVEL SECURITY;
                CREATE POLICY source_organization_isolation ON {schema}.sources
                  USING ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid)
                  WITH CHECK ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid);
                INSERT INTO {schema}.targets VALUES
                  ('{Guid.CreateVersion7()}', '{organizationA}', 'target-a'),
                  ('{Guid.CreateVersion7()}', '{organizationB}', 'target-b');
                INSERT INTO {schema}.sources VALUES ('{sourceId}', '{organizationA}', 'target-a');
                GRANT USAGE ON SCHEMA {schema} TO {role};
                GRANT SELECT, UPDATE ON {schema}.sources TO {role};
                """);

            NpgsqlConnectionStringBuilder runtimeBuilder = new(server.ConnectionString)
            {
                Username = role,
                Password = "fk-runtime-test",
                Pooling = false
            };
            await using NpgsqlConnection runtime = new(runtimeBuilder.ConnectionString);
            await runtime.OpenAsync();
            await using NpgsqlTransaction transaction = await runtime.BeginTransactionAsync();
            await SetContextAsync(runtime, transaction, organizationA, Guid.CreateVersion7());
            IsolationFixture source = new(
                new("sources", schema, "sources", ModuleDataOwnership.Organization,
                    "Fixture.Source", "source_organization_isolation"),
                [new("Id", "uuid", "uuid", false, false, false, false)],
                "TargetExternalKey",
                []);
            ForeignKeyFixture foreignKey = new(
                "sources_target_external_key_fkey",
                $"{schema}.targets",
                ["TargetExternalKey"],
                ["ExternalKey"]);
            FixtureRow targetB = new(Guid.CreateVersion7(), new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["ExternalKey"] = "target-b"
            });

            bool blocked = await CrossOrganizationAttachmentIsBlockedAsync(
                runtime, transaction, source, foreignKey, sourceId, targetB);

            blocked.ShouldBeFalse(
                "an insecure FK to a unique non-Id key must be executed and reported unsafe, not silently treated as blocked");
            await transaction.RollbackAsync();
        }
        finally
        {
            await ExecuteAsync(owner, null, $"DROP SCHEMA IF EXISTS {schema} CASCADE; DROP ROLE IF EXISTS {role}");
        }
    }

    private static async Task MigrateAsync(
        string connectionString,
        ModuleCatalog catalog,
        IReadOnlyList<IApplicationModelContributor> contributors)
    {
        await PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(connectionString);
        await using IdentityDbContext identity = new(
            new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(connectionString).Options);
        await identity.Database.MigrateAsync();
        await using PlatformDbContext platform = new(
            new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(connectionString).Options);
        await platform.Database.MigrateAsync();
        await using ApplicationDbContext application = new(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connectionString).Options,
            contributors,
            moduleCatalog: catalog);
        await application.Database.MigrateAsync();

        await using NpgsqlConnection owner = new(connectionString);
        await owner.OpenAsync();
        foreach (PendingModuleMigration migration in ModuleMigrationPlan.Build(catalog.Modules, []))
            await ExecuteAsync(owner, null, migration.Sql);
    }

    private static async Task SetContextAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid actorId)
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT set_config('app.organization_id', @organization, true), set_config('app.actor_id', @actor, true)";
        command.Parameters.AddWithValue("organization", organizationId.ToString());
        command.Parameters.AddWithValue("actor", actorId.ToString());
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string relation)
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT count(*) FROM {relation}";
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<string[]> ReadMarkersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IsolationFixture fixture)
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {QuoteIdentifier(fixture.MarkerColumn)} FROM {fixture.Relation} ORDER BY {QuoteIdentifier(fixture.MarkerColumn)}";
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        List<string> markers = [];
        while (await reader.ReadAsync())
            markers.Add(reader.GetString(0));
        return markers.ToArray();
    }

    private static Type ResolveEntityType(string fullName) =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(fullName, throwOnError: false))
            .FirstOrDefault(type => type is not null)
        ?? throw new InvalidOperationException($"Declared entity type '{fullName}' is not loaded.");

    private static async Task<int> CountEntityAsync(ApplicationDbContext dbContext, Type entityType)
    {
        var method = typeof(GeneratedOrganizationIsolationTests)
            .GetMethod(nameof(CountEntityAsyncCore), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(entityType);
        return await (Task<int>)method.Invoke(null, [dbContext])!;
    }

    private static Task<int> CountEntityAsyncCore<TEntity>(ApplicationDbContext dbContext) where TEntity : class =>
        dbContext.Set<TEntity>().AsNoTracking().CountAsync();

    private static async Task<IsolationFixture> DescribeFixtureAsync(
        NpgsqlConnection connection,
        DataResourceDescriptor resource)
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT column_name, data_type, udt_name,
                   is_nullable = 'YES', column_default IS NOT NULL,
                   is_identity = 'YES', is_generated <> 'NEVER'
            FROM information_schema.columns
            WHERE table_schema = @schema AND table_name = @table
            ORDER BY ordinal_position
            """;
        command.Parameters.AddWithValue("schema", resource.Schema);
        command.Parameters.AddWithValue("table", resource.Table);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        List<FixtureColumn> columns = [];
        while (await reader.ReadAsync())
            columns.Add(new(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5), reader.GetBoolean(6)));
        if (columns.Count == 0)
            throw new InvalidOperationException($"Declared relation '{resource.Schema}.{resource.Table}' does not exist.");

        string[] preferredMarkers = ["Name", "Title", "SubjectDisplayName"];
        string marker = preferredMarkers.FirstOrDefault(name => columns.Any(column => column.Name == name))
            ?? columns.FirstOrDefault(column => column.DataType is "text" or "character varying" or "character")?.Name
            ?? throw new InvalidOperationException($"Generated isolation fixture requires a textual marker column for '{resource.Schema}.{resource.Table}'.");
        await reader.DisposeAsync();

        await using NpgsqlCommand foreignKeysCommand = connection.CreateCommand();
        foreignKeysCommand.CommandText = """
            SELECT constraint_record.conname,
                   target_namespace.nspname || '.' || target.relname,
                   array_agg(source_attribute.attname ORDER BY keys.ordinality),
                   array_agg(target_attribute.attname ORDER BY keys.ordinality)
            FROM pg_constraint constraint_record
            JOIN pg_class source ON source.oid = constraint_record.conrelid
            JOIN pg_namespace source_namespace ON source_namespace.oid = source.relnamespace
            JOIN pg_class target ON target.oid = constraint_record.confrelid
            JOIN pg_namespace target_namespace ON target_namespace.oid = target.relnamespace
            JOIN LATERAL unnest(constraint_record.conkey, constraint_record.confkey)
              WITH ORDINALITY AS keys(source_number, target_number, ordinality) ON true
            JOIN pg_attribute source_attribute
              ON source_attribute.attrelid = source.oid AND source_attribute.attnum = keys.source_number
            JOIN pg_attribute target_attribute
              ON target_attribute.attrelid = target.oid AND target_attribute.attnum = keys.target_number
            WHERE constraint_record.contype = 'f'
              AND source_namespace.nspname = @schema AND source.relname = @table
            GROUP BY constraint_record.conname, target_namespace.nspname, target.relname
            ORDER BY constraint_record.conname
            """;
        foreignKeysCommand.Parameters.AddWithValue("schema", resource.Schema);
        foreignKeysCommand.Parameters.AddWithValue("table", resource.Table);
        await using NpgsqlDataReader foreignKeyReader = await foreignKeysCommand.ExecuteReaderAsync();
        List<ForeignKeyFixture> foreignKeys = [];
        while (await foreignKeyReader.ReadAsync())
            foreignKeys.Add(new(
                foreignKeyReader.GetString(0),
                foreignKeyReader.GetString(1),
                foreignKeyReader.GetFieldValue<string[]>(2),
                foreignKeyReader.GetFieldValue<string[]>(3)));
        return new(resource, columns, marker, foreignKeys);
    }

    private static async Task<bool> CrossOrganizationAttachmentIsBlockedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IsolationFixture source,
        ForeignKeyFixture foreignKey,
        Guid sourceId,
        FixtureRow targetRow)
    {
        if (!source.Columns.Any(column => string.Equals(column.Name, "Id", StringComparison.Ordinal)))
            throw new InvalidOperationException(
                $"Generated isolation fixture cannot attempt '{source.QualifiedName}/{foreignKey.Name}' because the source has no Id column.");

        object[] values = foreignKey.TargetColumns.Select(column =>
        {
            if (!targetRow.Values.TryGetValue(column, out object? value) || value is null or DBNull)
                throw new InvalidOperationException(
                    $"Generated isolation fixture cannot synthesize referenced column '{foreignKey.TargetRelation}.{column}' for '{source.QualifiedName}/{foreignKey.Name}'.");
            return value;
        }).ToArray();

        await ExecuteAsync(connection, transaction, "SAVEPOINT cross_organization_attachment");
        try
        {
            await using NpgsqlCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            List<string> assignments = [];
            for (int index = 0; index < foreignKey.SourceColumns.Count; index++)
            {
                assignments.Add($"{QuoteIdentifier(foreignKey.SourceColumns[index])} = @value{index}");
                command.Parameters.AddWithValue($"value{index}", values[index]);
            }
            command.Parameters.AddWithValue("sourceId", sourceId);
            command.CommandText = $"UPDATE {source.Relation} SET {string.Join(", ", assignments)} WHERE \"Id\" = @sourceId";
            int affected = await command.ExecuteNonQueryAsync();
            await ExecuteAsync(connection, transaction, "ROLLBACK TO SAVEPOINT cross_organization_attachment");
            return affected == 0;
        }
        catch (PostgresException)
        {
            await ExecuteAsync(connection, transaction, "ROLLBACK TO SAVEPOINT cross_organization_attachment");
            return true;
        }
    }

    private static async Task<FixtureRow> ReadFixtureRowAsync(
        NpgsqlConnection connection,
        IsolationFixture fixture,
        Guid id)
    {
        if (!fixture.Columns.Any(column => string.Equals(column.Name, "Id", StringComparison.Ordinal)))
            throw new InvalidOperationException(
                $"Generated isolation fixture cannot identify rows in '{fixture.QualifiedName}' because it has no Id column.");
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {fixture.Relation} WHERE \"Id\" = @id";
        command.Parameters.AddWithValue("id", id);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException($"Generated isolation fixture row '{fixture.QualifiedName}/{id}' was not inserted.");
        Dictionary<string, object> values = new(StringComparer.Ordinal);
        for (int index = 0; index < reader.FieldCount; index++)
            values[reader.GetName(index)] = reader.GetValue(index);
        return new(id, values);
    }

    private static async Task InsertFixtureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IsolationFixture fixture,
        Guid id,
        Guid organizationId,
        Guid actorId,
        string marker)
    {
        FixtureColumn[] populated = fixture.Columns
            .Where(column => string.Equals(column.Name, fixture.MarkerColumn, StringComparison.Ordinal)
                || (!column.HasDefault && !column.IsIdentity && !column.IsGenerated && !column.IsNullable))
            .ToArray();
        List<string> values = [];
        await using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        foreach (FixtureColumn column in populated)
        {
            string parameter = $"p{values.Count}";
            object? value = column.Name switch
            {
                "OrganizationId" => organizationId,
                "Id" => id,
                "ActorId" or "CreatedBy" => actorId,
                _ => column.DataType switch
                {
                    "text" or "character varying" or "character" => marker,
                    "timestamp with time zone" or "timestamp without time zone" => DateTimeOffset.UtcNow,
                    "boolean" => false,
                    "smallint" or "integer" or "bigint" or "numeric" => 0,
                    _ when column.UdtName is "json" or "jsonb" => "{}",
                    _ => throw new InvalidOperationException(
                        $"Generated isolation fixture cannot synthesize required column '{fixture.Relation}.{column.Name}' ({column.DataType}/{column.UdtName}).")
                }
            };
            string expression = column.UdtName is "json" or "jsonb"
                ? $"CAST(@{parameter} AS {column.UdtName})"
                : $"@{parameter}";
            values.Add(expression);
            command.Parameters.AddWithValue(parameter, value);
        }
        command.CommandText = $"INSERT INTO {fixture.Relation} ({string.Join(", ", populated.Select(column => QuoteIdentifier(column.Name)))}) VALUES ({string.Join(", ", values)})";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> MutateFixtureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IsolationFixture fixture,
        Guid organizationId,
        bool delete)
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = delete
            ? $"DELETE FROM {fixture.Relation} WHERE \"OrganizationId\" = @organization"
            : $"UPDATE {fixture.Relation} SET {QuoteIdentifier(fixture.MarkerColumn)} = 'updated' WHERE \"OrganizationId\" = @organization";
        command.Parameters.AddWithValue("organization", organizationId);
        return await command.ExecuteNonQueryAsync();
    }

    private static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        Guid? id = null,
        Guid? organizationId = null,
        Guid? actorId = null,
        string? marker = null)
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (id is not null) command.Parameters.AddWithValue("id", id.Value);
        if (organizationId is not null) command.Parameters.AddWithValue("organization", organizationId.Value);
        if (actorId is not null) command.Parameters.AddWithValue("actor", actorId.Value);
        if (marker is not null) command.Parameters.AddWithValue("marker", marker);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record IsolationFixture(
        DataResourceDescriptor Resource,
        IReadOnlyList<FixtureColumn> Columns,
        string MarkerColumn,
        IReadOnlyList<ForeignKeyFixture> ForeignKeys)
    {
        public string QualifiedName => $"{Resource.Schema}.{Resource.Table}";
        public string Relation => $"{QuoteIdentifier(Resource.Schema)}.{QuoteIdentifier(Resource.Table)}";
    }

    private sealed record ForeignKeyFixture(
        string Name,
        string TargetRelation,
        IReadOnlyList<string> SourceColumns,
        IReadOnlyList<string> TargetColumns);

    private sealed record FixtureRow(Guid Id, IReadOnlyDictionary<string, object> Values);

    private sealed record FixtureColumn(
        string Name,
        string DataType,
        string UdtName,
        bool IsNullable,
        bool HasDefault,
        bool IsIdentity,
        bool IsGenerated);

    private sealed class DatabaseServer(PostgreSqlContainer? container, string connectionString) : IAsyncDisposable
    {
        public string ConnectionString { get; } = connectionString;

        public static async Task<DatabaseServer> StartAsync()
        {
            string? external = Environment.GetEnvironmentVariable("TRYKATCH_TEST_POSTGRES");
            if (!string.IsNullOrWhiteSpace(external)) return new(null, external);
            PostgreSqlContainer container = new PostgreSqlBuilder(
                "postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f").Build();
            await container.StartAsync();
            return new(container, container.GetConnectionString());
        }

        public async ValueTask DisposeAsync()
        {
            if (container is not null) await container.DisposeAsync();
        }
    }
}
