using FlatpackApp.Modules;
using Npgsql;

namespace FlatpackApp.Migrator;

internal static class ModuleMigrationExecutor
{
    private const string LockName = "flatpack:module-migrations:v1";

    public static async Task ApplyAsync(
        string connectionString,
        IReadOnlyList<IFlatpackModule> enabledModules,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(enabledModules);

        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

        await ExecuteAsync(connection, transaction, "SELECT pg_advisory_xact_lock(hashtext(@lock_name));", cancellationToken,
            ("lock_name", LockName));
        await ExecuteAsync(connection, transaction, """
            CREATE TABLE IF NOT EXISTS platform.module_migrations
            (
                module_id text NOT NULL,
                module_version text NOT NULL,
                migration_id text NOT NULL,
                checksum character(64) NOT NULL,
                applied_at timestamp with time zone NOT NULL DEFAULT now(),
                CONSTRAINT pk_module_migrations PRIMARY KEY (module_id, migration_id)
            );
            """, cancellationToken);

        HashSet<string> enabledIds = enabledModules
            .Select(module => module.Descriptor.Id)
            .ToHashSet(StringComparer.Ordinal);
        List<AppliedFlatpackModuleMigration> applied = [];
        await using (NpgsqlCommand history = connection.CreateCommand())
        {
            history.Transaction = transaction;
            history.CommandText = "SELECT module_id, migration_id, checksum FROM platform.module_migrations ORDER BY module_id, migration_id";
            await using NpgsqlDataReader reader = await history.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                string moduleId = reader.GetString(0);
                if (enabledIds.Contains(moduleId))
                    applied.Add(new(moduleId, reader.GetString(1), reader.GetString(2)));
            }
        }

        IReadOnlyList<PendingFlatpackModuleMigration> pending =
            FlatpackModuleMigrationPlan.Build(enabledModules, applied);
        foreach (PendingFlatpackModuleMigration migration in pending)
        {
            await ExecuteAsync(connection, transaction, migration.Sql, cancellationToken);
            await ExecuteAsync(connection, transaction, """
                INSERT INTO platform.module_migrations
                    (module_id, module_version, migration_id, checksum)
                VALUES
                    (@module_id, @module_version, @migration_id, @checksum);
                """, cancellationToken,
                ("module_id", migration.ModuleId),
                ("module_version", migration.ModuleVersion),
                ("migration_id", migration.MigrationId),
                ("checksum", migration.Checksum));
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
            command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
