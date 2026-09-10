using System.Text.Json;
using Npgsql;
using TrykatchApp.Modules;

namespace TrykatchApp.Infrastructure.Organizations;

/// <summary>
/// Migrator-owned durable declarations survive module disable/unregister. Reading
/// this metadata does not construct, register or execute a disabled module.
/// </summary>
public static class InstalledSchemaCatalog
{
    public static async Task<IReadOnlyList<TrykatchDataResourceDescriptor>> ReadAsync(
        string connectionString, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        return await ReadAsync(connection, cancellationToken);
    }

    public static async Task SynchronizeAsync(
        string connectionString,
        IEnumerable<TrykatchInstalledDataResource> installed,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using NpgsqlCommand initialize = new("""
            SELECT pg_advisory_xact_lock(hashtext('trykatch:installed-schema:v1'));
            CREATE TABLE IF NOT EXISTS platform.module_data_resources (
                schema_name text NOT NULL, table_name text NOT NULL,
                module_id text NOT NULL, declaration jsonb NOT NULL,
                PRIMARY KEY (schema_name, table_name));
            """, connection, transaction);
        await initialize.ExecuteNonQueryAsync(cancellationToken);

        foreach (TrykatchInstalledDataResource item in installed)
        {
            TrykatchDataResourceRules.Validate(item.Resource);
            await using NpgsqlCommand upsert = new("""
                INSERT INTO platform.module_data_resources (schema_name, table_name, module_id, declaration)
                SELECT @schema, @table, @module, CAST(@declaration AS jsonb)
                WHERE EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                  WHERE n.nspname = @schema AND c.relname = @table AND c.relkind IN ('r', 'p'))
                ON CONFLICT DO NOTHING;
                SELECT module_id = @module AND declaration = CAST(@declaration AS jsonb)
                FROM platform.module_data_resources WHERE schema_name = @schema AND table_name = @table;
                """, connection, transaction);
            upsert.Parameters.AddWithValue("schema", item.Resource.Schema);
            upsert.Parameters.AddWithValue("table", item.Resource.Table);
            upsert.Parameters.AddWithValue("module", item.ModuleId);
            upsert.Parameters.AddWithValue("declaration", JsonSerializer.Serialize(item.Resource));
            if (await upsert.ExecuteScalarAsync(cancellationToken) is not true)
                throw new InvalidOperationException(
                    $"Installed relation '{item.Resource.Schema}.{item.Resource.Table}' is absent or its ownership declaration changed. An explicit reviewed data migration is required.");
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public static async Task ValidateDeclaredObjectsAsync(
        string connectionString,
        IEnumerable<string> declaredRelations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(declaredRelations);
        HashSet<string> declared = declaredRelations.ToHashSet(StringComparer.Ordinal);
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = new($"""
            SELECT n.nspname || '.' || c.relname,
                   CASE WHEN c.relkind = 'S' AND target.oid IS NOT NULL
                     THEN target_namespace.nspname || '.' || target.relname ELSE NULL END
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_depend dependency ON dependency.classid = 'pg_class'::regclass
              AND dependency.objid = c.oid AND dependency.deptype IN ('a', 'i')
            LEFT JOIN pg_class target ON target.oid = dependency.refobjid
            LEFT JOIN pg_namespace target_namespace ON target_namespace.oid = target.relnamespace
            WHERE c.relkind IN ('r', 'p', 'v', 'm', 'f', 'S')
              AND {PostgresSchemaContract.UserSchemaPredicate}
            ORDER BY n.nspname, c.relname
            """, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        List<string> undeclared = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            string relation = reader.GetString(0);
            string? sequenceOwner = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (!declared.Contains(relation)
                && (sequenceOwner is null || !declared.Contains(sequenceOwner)))
                undeclared.Add(relation);
        }
        await reader.DisposeAsync();
        await using NpgsqlCommand functions = new($"""
            SELECT n.nspname || '.' || p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ')'
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE {PostgresSchemaContract.UserSchemaPredicate}
            ORDER BY 1
            """, connection);
        await using NpgsqlDataReader functionReader = await functions.ExecuteReaderAsync(cancellationToken);
        while (await functionReader.ReadAsync(cancellationToken)) undeclared.Add(functionReader.GetString(0));
        if (undeclared.Count > 0)
            throw new InvalidOperationException(
                "PostgreSQL user schemas contain undeclared persistent objects: " + string.Join(", ", undeclared) +
                ". Runtime grants were not applied.");
    }

    internal static async Task<IReadOnlyList<TrykatchDataResourceDescriptor>> ReadAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand exists = new("SELECT to_regclass('platform.module_data_resources') IS NOT NULL", connection);
        if (await exists.ExecuteScalarAsync(cancellationToken) is not true) return [];
        await using NpgsqlCommand read = new("SELECT declaration::text FROM platform.module_data_resources ORDER BY schema_name, table_name", connection);
        await using NpgsqlDataReader reader = await read.ExecuteReaderAsync(cancellationToken);
        List<TrykatchDataResourceDescriptor> result = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            TrykatchDataResourceDescriptor resource = JsonSerializer.Deserialize<TrykatchDataResourceDescriptor>(reader.GetString(0))
                ?? throw new InvalidOperationException("Installed schema declaration is invalid.");
            TrykatchDataResourceRules.Validate(resource);
            result.Add(resource);
        }
        return result;
    }
}
