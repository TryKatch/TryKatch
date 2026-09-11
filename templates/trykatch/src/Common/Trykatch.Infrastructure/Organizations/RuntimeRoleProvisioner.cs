using System.Text.RegularExpressions;
using Npgsql;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Organizations;

/// <summary>
/// Applies the closed runtime profiles after migrations and durable schema
/// declarations have been validated. Tests and production cross the same seam.
/// </summary>
public static partial class RuntimeRoleProvisioner
{
    public static async Task ProvisionAsync(
        string connectionString,
        RuntimeDatabaseRoles roles,
        CancellationToken cancellationToken = default)
    {
        string[] roleNames = [roles.Organization, roles.Platform, roles.Identity, roles.Outbox];
        if (roleNames.Distinct(StringComparer.Ordinal).Count() != roleNames.Length)
            throw new InvalidOperationException("Database runtime roles must be distinct.");
        foreach (string roleName in roleNames)
        {
            if (!RoleNamePattern().IsMatch(roleName))
                throw new InvalidOperationException("Database runtime role must be a lowercase PostgreSQL identifier.");
        }

        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (string roleName in roleNames)
            await VerifyRoleAsync(connection, roleName, cancellationToken);
        if (roles.Platform != "trykatch_platform_runtime")
            throw new InvalidOperationException("Platform role must match the host policy contract: trykatch_platform_runtime.");

        string organization = QuoteIdentifier(roles.Organization);
        string platform = QuoteIdentifier(roles.Platform);
        string identity = QuoteIdentifier(roles.Identity);
        string outbox = QuoteIdentifier(roles.Outbox);
        bool hostFunctionExists = await HostFunctionExistsAsync(connection, cancellationToken);
        if (hostFunctionExists)
        {
            await using NpgsqlCommand quarantineFunction = new($"""
                REVOKE ALL ON FUNCTION platform.redact_legacy_outbox_error()
                FROM {organization}, {platform}, {identity}, {outbox}, PUBLIC;
                """, connection);
            await quarantineFunction.ExecuteNonQueryAsync(cancellationToken);
        }

        List<string> hostFunctionErrors = [];
        await PostgresIsolationInspector.InspectHostFunctionContractsAsync(
            connection,
            hostFunctionErrors,
            cancellationToken);
        if (hostFunctionErrors.Count > 0)
            throw new InvalidOperationException(
                "PostgreSQL host function validation failed:" + Environment.NewLine
                + string.Join(Environment.NewLine, hostFunctionErrors));

        IReadOnlyList<string> userSchemas = await ReadUserSchemasAsync(connection, cancellationToken);
        foreach (string schema in userSchemas)
        {
            await using NpgsqlCommand revoke = new($"""
                REVOKE ALL PRIVILEGES ON SCHEMA {QuoteIdentifier(schema)} FROM {organization}, {platform}, {identity}, {outbox}, PUBLIC;
                REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA {QuoteIdentifier(schema)} FROM {organization}, {platform}, {identity}, {outbox}, PUBLIC;
                REVOKE ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA {QuoteIdentifier(schema)} FROM {organization}, {platform}, {identity}, {outbox}, PUBLIC;
                REVOKE ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA {QuoteIdentifier(schema)} FROM {organization}, {platform}, {identity}, {outbox}, PUBLIC;
                ALTER DEFAULT PRIVILEGES IN SCHEMA {QuoteIdentifier(schema)} REVOKE ALL ON TABLES FROM {organization}, {platform}, {identity}, {outbox}, PUBLIC;
                ALTER DEFAULT PRIVILEGES IN SCHEMA {QuoteIdentifier(schema)} REVOKE ALL ON SEQUENCES FROM {organization}, {platform}, {identity}, {outbox}, PUBLIC;
                ALTER DEFAULT PRIVILEGES IN SCHEMA {QuoteIdentifier(schema)} REVOKE ALL ON FUNCTIONS FROM {organization}, {platform}, {identity}, {outbox}, PUBLIC;
                """, connection);
            await revoke.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (NpgsqlCommand databaseGrants = new($"""
            REVOKE ALL PRIVILEGES ON DATABASE {QuoteIdentifier(connection.Database)} FROM {organization}, {platform}, {identity}, {outbox}, PUBLIC;
            GRANT CONNECT ON DATABASE {QuoteIdentifier(connection.Database)} TO {organization}, {platform}, {identity}, {outbox};
            GRANT USAGE ON SCHEMA identity TO {identity};
            GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA identity TO {identity};
            """, connection))
            await databaseGrants.ExecuteNonQueryAsync(cancellationToken);

        IReadOnlyList<DataResourceDescriptor> installed =
            await InstalledSchemaCatalog.ReadAsync(connectionString, cancellationToken);
        Dictionary<string, DataResourceDescriptor> resources = installed.ToDictionary(
            resource => $"{resource.Schema}.{resource.Table}", StringComparer.Ordinal);
        IReadOnlyList<(string Schema, string Table)> relations = await ReadRelationsAsync(connection, cancellationToken);
        foreach ((RuntimeDatabaseRoleKind kind, string roleName) in roles.All)
        {
            string quotedRole = QuoteIdentifier(roleName);
            HashSet<string> grantedSchemas = new(StringComparer.Ordinal);
            foreach ((string schema, string table) in relations)
            {
                string relation = $"{schema}.{table}";
                IReadOnlySet<string> permissions = RuntimeDatabaseAccessProfiles.PermissionsFor(kind, relation, resources);
                if (permissions.Count == 0) continue;
                if (grantedSchemas.Add(schema))
                {
                    await using NpgsqlCommand schemaGrant = new(
                        $"GRANT USAGE ON SCHEMA {QuoteIdentifier(schema)} TO {quotedRole}", connection);
                    await schemaGrant.ExecuteNonQueryAsync(cancellationToken);
                }
                await using NpgsqlCommand tableGrant = new(
                    $"GRANT {string.Join(", ", permissions)} ON {QuoteIdentifier(schema)}.{QuoteIdentifier(table)} TO {quotedRole}", connection);
                await tableGrant.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        if (hostFunctionExists)
        {
            await using NpgsqlCommand functionGrant = new($"""
                GRANT EXECUTE ON FUNCTION platform.redact_legacy_outbox_error() TO {organization}, {outbox};
                """, connection);
            await functionGrant.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task VerifyRoleAsync(
        NpgsqlConnection connection,
        string roleName,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand verify = connection.CreateCommand();
        verify.CommandText = """
            SELECT rolbypassrls OR rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolinherit OR NOT rolcanlogin
              OR EXISTS (SELECT 1 FROM pg_auth_members m WHERE m.member = r.oid)
              OR EXISTS (SELECT 1 FROM pg_class c WHERE c.relowner = r.oid)
              OR EXISTS (SELECT 1 FROM pg_namespace n WHERE n.nspowner = r.oid)
              OR EXISTS (SELECT 1 FROM pg_database d WHERE d.datdba = r.oid)
            FROM pg_roles r WHERE rolname = @role_name
            """;
        verify.Parameters.AddWithValue("role_name", roleName);
        if (await verify.ExecuteScalarAsync(cancellationToken) is not bool invalid)
            throw new InvalidOperationException($"Database runtime role '{roleName}' must be created by the bootstrap administrator before migrations run.");
        if (invalid)
            throw new InvalidOperationException($"Database runtime role '{roleName}' must not own objects, have administrative attributes or belong to other roles.");
    }

    private static async Task<IReadOnlyList<string>> ReadUserSchemasAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new($"""
            SELECT n.nspname FROM pg_namespace n
            WHERE {PostgresSchemaContract.UserSchemaPredicate}
            ORDER BY n.nspname
            """, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        List<string> result = [];
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
        return result;
    }

    private static async Task<IReadOnlyList<(string Schema, string Table)>> ReadRelationsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new($"""
            SELECT n.nspname, c.relname
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r', 'p', 'v', 'm', 'f')
              AND {PostgresSchemaContract.UserSchemaPredicate}
            ORDER BY n.nspname, c.relname
            """, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        List<(string Schema, string Table)> result = [];
        while (await reader.ReadAsync(cancellationToken)) result.Add((reader.GetString(0), reader.GetString(1)));
        return result;
    }

    private static async Task<bool> HostFunctionExistsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(
            "SELECT to_regprocedure(@function) IS NOT NULL", connection);
        command.Parameters.AddWithValue(
            "function",
            HostPostgresFunctionContracts.RedactLegacyOutboxError);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex RoleNamePattern();
}
