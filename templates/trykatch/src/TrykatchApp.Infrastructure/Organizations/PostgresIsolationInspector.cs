using Npgsql;
using TrykatchApp.Modules;

namespace TrykatchApp.Infrastructure.Organizations;

public sealed record PostgresIsolationInspection(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
    public void ThrowIfInvalid()
    {
        if (!IsValid)
            throw new InvalidOperationException(
                "PostgreSQL isolation inspection failed:" + Environment.NewLine + string.Join(Environment.NewLine, Errors));
    }
}

/// <summary>Inspects the migrated schema through PostgreSQL's own catalogs.</summary>
public static class PostgresIsolationInspector
{
    public static async Task<PostgresIsolationInspection> InspectAsync(
        string connectionString,
        string runtimeRole,
        IEnumerable<TrykatchModuleDescriptor> modules,
        CancellationToken cancellationToken = default) =>
        await InspectAsync(connectionString, [(RuntimeDatabaseRoleKind.Organization, runtimeRole)], modules, cancellationToken);

    public static async Task<PostgresIsolationInspection> InspectAsync(
        string connectionString,
        RuntimeDatabaseRoles runtimeRoles,
        IEnumerable<TrykatchModuleDescriptor> modules,
        CancellationToken cancellationToken = default) =>
        await InspectAsync(connectionString, runtimeRoles.All, modules, cancellationToken);

    private static async Task<PostgresIsolationInspection> InspectAsync(
        string connectionString,
        IEnumerable<(RuntimeDatabaseRoleKind Kind, string Name)> runtimeRoles,
        IEnumerable<TrykatchModuleDescriptor> modules,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(modules);
        (RuntimeDatabaseRoleKind Kind, string Name)[] roles = runtimeRoles.ToArray();
        if (roles.Length == 0 || roles.Any(role => string.IsNullOrWhiteSpace(role.Name)))
            throw new ArgumentException("Runtime role names are required.", nameof(runtimeRoles));

        TrykatchDataResourceDescriptor audit = new(
            "audit-entries", "platform", "audit_entries", TrykatchDataOwnership.Organization,
            "TrykatchApp.Domain.Organizations.AuditEntry", "audit_organization_isolation");
        TrykatchDataResourceDescriptor outbox = new(
            "outbox-messages", "platform", "outbox_messages", TrykatchDataOwnership.Infrastructure,
            AccessRule: TrykatchDataAccessRule.OutboxAppendOnly);
        TrykatchDataResourceDescriptor[] resources = modules.SelectMany(module => module.DataResources)
            .Append(audit)
            .Append(outbox)
            .ToArray();
        List<string> errors = [];

        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);

        IReadOnlyList<TrykatchDataResourceDescriptor> retained = await InstalledSchemaCatalog.ReadAsync(connection, cancellationToken);
        Dictionary<string, TrykatchDataResourceDescriptor> declarations = new(StringComparer.Ordinal);
        foreach (TrykatchDataResourceDescriptor resource in retained.Concat(resources))
        {
            string key = $"{resource.Schema}.{resource.Table}";
            TrykatchDataResourceRules.Validate(resource, hostOwned: resource == audit || resource == outbox);
            if (declarations.TryGetValue(key, out TrykatchDataResourceDescriptor? existing) && existing != resource)
                errors.Add($"Enabled and installed schema declarations disagree for '{key}'.");
            declarations[key] = resource;
        }
        resources = declarations.Values.ToArray();

        Dictionary<string, RelationPolicy> relations = await ReadRelationsAsync(connection, cancellationToken);
        IReadOnlyList<SequenceInfo> sequences = await ReadSequencesAsync(connection, cancellationToken);
        IReadOnlyList<string> functions = await ReadUserFunctionsAsync(connection, cancellationToken);
        IReadOnlyList<string> schemas = await ReadUserSchemasAsync(connection, cancellationToken);
        foreach ((RuntimeDatabaseRoleKind kind, string role) in roles)
        {
            await InspectRoleAsync(connection, role, errors, cancellationToken);
            await InspectAccessAsync(connection, role, kind, declarations, relations.Keys, sequences, schemas, errors, cancellationToken);
        }
        foreach ((string relationName, HostPostgresPolicyContracts.Policy[] expected) in HostPostgresPolicyContracts.All)
        {
            if (!relations.TryGetValue(relationName, out RelationPolicy? relation)) continue;
            if (!relation.RowSecurity || !relation.ForceRowSecurity || relation.Policies.Count != expected.Length)
                errors.Add($"Host relation '{relationName}' must have forced RLS and exactly its approved policies.");
            foreach (HostPostgresPolicyContracts.Policy contract in expected)
            {
                Policy? actual = relation.Policies.SingleOrDefault(policy => policy.Name == contract.Name);
                if (actual is null || actual.Command != contract.Command || !actual.Permissive || !actual.AppliesToPublic
                    || NormalizePolicy(actual.Using, preserveParentheses: true) != NormalizePolicy(contract.Using, preserveParentheses: true)
                    || NormalizePolicy(actual.WithCheck, preserveParentheses: true) != NormalizePolicy(contract.WithCheck, preserveParentheses: true))
                    errors.Add($"Host policy '{relationName}/{contract.Name}' differs from the approved command, role or expression contract.");
            }
        }
        foreach (TrykatchDataResourceDescriptor resource in resources)
        {
            string key = $"{resource.Schema}.{resource.Table}";
            if (!relations.TryGetValue(key, out RelationPolicy? relation))
            {
                errors.Add($"Declared relation '{key}' is absent.");
                continue;
            }
            if (resource.Ownership != TrykatchDataOwnership.Organization)
                continue;
            if (HostPostgresPolicyContracts.All.ContainsKey(key)) continue;
            if (!relation.RowSecurity || !relation.ForceRowSecurity)
                errors.Add($"Organization relation '{key}' must ENABLE and FORCE row-level security.");
            // A second permissive policy is OR-ed with the isolation policy by
            // PostgreSQL. A policy for a different command/role is not equivalent
            // to the host's all-command, all-runtime-path isolation contract.
            if (relation.Policies.Count != 1)
            {
                errors.Add($"Organization relation '{key}' must have exactly one host-approved isolation policy.");
                continue;
            }
            Policy policy = relation.Policies[0];
            if (!string.Equals(policy.Name, resource.IsolationPolicy, StringComparison.Ordinal)
                || policy.Command != "*" || !policy.Permissive || !policy.AppliesToPublic)
                errors.Add($"Organization relation '{key}' requires policy '{resource.IsolationPolicy}' FOR ALL TO PUBLIC.");
            if (!EnforcesOrganization(policy.Using) || !EnforcesOrganization(policy.WithCheck))
                errors.Add($"Policy '{resource.IsolationPolicy}' on '{key}' must enforce app.organization_id in both USING and WITH CHECK.");
        }

        foreach ((string relationName, RelationPolicy relation) in relations)
        {
            foreach (Policy policy in relation.Policies)
            {
                if ((policy.Using + policy.WithCheck).Contains("app.platform_admin", StringComparison.OrdinalIgnoreCase))
                    errors.Add($"Policy '{policy.Name}' on '{relationName}' contains the forbidden app.platform_admin bypass.");
            }
        }

        foreach (string relation in relations.Keys)
        {
            if (!declarations.ContainsKey(relation) && !RuntimeDatabaseAccessProfiles.HostOwnedRelations.Contains(relation))
                errors.Add($"User-schema relation '{relation}' is not declared by the host or an installed module.");
        }
        foreach (SequenceInfo sequence in sequences)
        {
            if (sequence.OwnedByRelation is null
                || !declarations.ContainsKey(sequence.OwnedByRelation)
                    && !RuntimeDatabaseAccessProfiles.HostOwnedRelations.Contains(sequence.OwnedByRelation))
                errors.Add($"User-schema sequence '{sequence.Relation}' is not owned by a declared relation.");
        }
        foreach (string function in functions)
            errors.Add($"User-schema function '{function}' is not declared by the host.");

        return new(errors);
    }

    private static async Task InspectAccessAsync(
        NpgsqlConnection connection,
        string runtimeRole,
        RuntimeDatabaseRoleKind kind,
        IReadOnlyDictionary<string, TrykatchDataResourceDescriptor> resources,
        IEnumerable<string> relations,
        IReadOnlyList<SequenceInfo> sequences,
        IReadOnlyList<string> schemas,
        List<string> errors, CancellationToken cancellationToken)
    {
        HashSet<string> relationSet = relations.ToHashSet(StringComparer.Ordinal);
        await using NpgsqlCommand command = new($"""
            SELECT n.nspname || '.' || c.relname, permission.name,
              has_table_privilege(@role, c.oid, permission.name)
                OR CASE WHEN permission.name = 'DELETE' THEN false ELSE has_any_column_privilege(@role, c.oid, permission.name) END
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            CROSS JOIN (VALUES ('SELECT'), ('INSERT'), ('UPDATE'), ('DELETE')) AS permission(name)
            WHERE c.relkind IN ('r', 'p', 'v', 'm', 'f')
              AND {PostgresSchemaContract.UserSchemaPredicate}
            """, connection);
        command.Parameters.AddWithValue("role", runtimeRole);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string relation = reader.GetString(0);
            string permission = reader.GetString(1);
            bool actual = reader.GetBoolean(2);
            bool expected = RuntimeDatabaseAccessProfiles.PermissionsFor(kind, relation, resources).Contains(permission);
            if (actual != expected)
                errors.Add($"{kind} runtime '{runtimeRole}' {(actual ? "has forbidden" : "is missing required")} {permission} access to '{relation}'.");
        }
        await reader.DisposeAsync();

        await using NpgsqlCommand sequenceAccess = new($"""
            SELECT n.nspname || '.' || c.relname, permission.name,
                   has_sequence_privilege(@role, c.oid, permission.name)
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            CROSS JOIN (VALUES ('USAGE'), ('SELECT'), ('UPDATE')) AS permission(name)
            WHERE c.relkind = 'S'
              AND {PostgresSchemaContract.UserSchemaPredicate}
            """, connection);
        sequenceAccess.Parameters.AddWithValue("role", runtimeRole);
        await using NpgsqlDataReader sequenceReader = await sequenceAccess.ExecuteReaderAsync(cancellationToken);
        while (await sequenceReader.ReadAsync(cancellationToken))
        {
            string sequence = sequenceReader.GetString(0);
            string permission = sequenceReader.GetString(1);
            bool actual = sequenceReader.GetBoolean(2);
            bool expected = RuntimeDatabaseAccessProfiles.SequencePermissionsFor(kind, sequence).Contains(permission);
            if (actual != expected)
                errors.Add($"{kind} runtime '{runtimeRole}' {(actual ? "has forbidden" : "is missing required")} {permission} access to sequence '{sequence}'.");
        }
        await sequenceReader.DisposeAsync();

        foreach (string schema in schemas)
        {
            bool expected = relationSet.Any(relation => relation.StartsWith(schema + ".", StringComparison.Ordinal)
                && RuntimeDatabaseAccessProfiles.PermissionsFor(kind, relation, resources).Count > 0)
                || sequences.Any(sequence => sequence.Relation.StartsWith(schema + ".", StringComparison.Ordinal)
                    && RuntimeDatabaseAccessProfiles.SequencePermissionsFor(kind, sequence.Relation).Count > 0);
            await using NpgsqlCommand schemaAccess = new("""
                SELECT COALESCE((SELECT has_schema_privilege(@role, n.oid, 'USAGE')
                  FROM pg_namespace n WHERE n.nspname = @schema), false)
                """, connection);
            schemaAccess.Parameters.AddWithValue("schema", schema);
            schemaAccess.Parameters.AddWithValue("role", runtimeRole);
            bool actual = await schemaAccess.ExecuteScalarAsync(cancellationToken) is true;
            if (actual != expected)
                errors.Add($"{kind} runtime '{runtimeRole}' {(actual ? "has forbidden" : "is missing required")} USAGE on schema '{schema}'.");
        }
    }

    private static async Task InspectRoleAsync(
        NpgsqlConnection connection,
        string role,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT r.rolsuper OR r.rolbypassrls OR r.rolcreatedb OR r.rolcreaterole OR r.rolreplication OR r.rolinherit OR NOT r.rolcanlogin,
                   EXISTS (SELECT 1 FROM pg_auth_members m WHERE m.member = r.oid),
                   EXISTS (
                     SELECT 1 FROM pg_class c WHERE c.relowner = r.oid)
                     OR EXISTS (SELECT 1 FROM pg_namespace n WHERE n.nspowner = r.oid)
                     OR EXISTS (SELECT 1 FROM pg_database d WHERE d.datdba = r.oid),
                   EXISTS (SELECT 1 FROM pg_namespace n
                     WHERE {PostgresSchemaContract.UserSchemaPredicate}
                       AND has_schema_privilege(r.oid, n.oid, 'CREATE'))
                     OR has_database_privilege(r.oid, current_database(), 'CREATE')
                     OR has_database_privilege(r.oid, current_database(), 'TEMPORARY'),
                   EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                     WHERE c.relkind IN ('r', 'p', 'v', 'm', 'f')
                       AND {PostgresSchemaContract.UserSchemaPredicate}
                       AND (has_table_privilege(r.oid, c.oid, 'TRUNCATE, REFERENCES, TRIGGER')
                         OR has_table_privilege(r.oid, c.oid, 'SELECT WITH GRANT OPTION, INSERT WITH GRANT OPTION, UPDATE WITH GRANT OPTION, DELETE WITH GRANT OPTION')
                         OR has_any_column_privilege(r.oid, c.oid, 'REFERENCES, SELECT WITH GRANT OPTION, INSERT WITH GRANT OPTION, UPDATE WITH GRANT OPTION'))),
                   EXISTS (SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                     WHERE (({PostgresSchemaContract.UserSchemaPredicate})
                       OR n.nspname = 'pg_catalog' AND p.proname IN ('pg_read_file', 'pg_read_binary_file', 'pg_ls_dir', 'pg_stat_file', 'lo_import', 'lo_export'))
                       AND has_function_privilege(r.oid, p.oid, 'EXECUTE'))
            FROM pg_roles r WHERE r.rolname = @role;
            """;
        command.Parameters.AddWithValue("role", role);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            errors.Add($"Runtime role '{role}' does not exist.");
            return;
        }
        if (reader.GetBoolean(0))
            errors.Add($"Runtime role '{role}' must be LOGIN, NOINHERIT, NOSUPERUSER, NOBYPASSRLS, NOCREATEDB, NOCREATEROLE and NOREPLICATION.");
        // Fail closed on all memberships, including NOINHERIT: SET ROLE is an
        // independent privilege and membership chains can change after inspection.
        if (reader.GetBoolean(1))
            errors.Add($"Runtime role '{role}' must not have role memberships or SET ROLE paths.");
        if (reader.GetBoolean(2))
            errors.Add($"Runtime role '{role}' must not own relations, schemas or databases.");
        if (reader.GetBoolean(3))
            errors.Add($"Runtime role '{role}' must not have schema/database CREATE or database TEMPORARY privileges.");
        if (reader.GetBoolean(4))
            errors.Add($"Runtime role '{role}' has dangerous table or grant-option privileges.");
        if (reader.GetBoolean(5))
            errors.Add($"Runtime role '{role}' can execute an unapproved managed-schema or privileged function.");
    }

    private static async Task<Dictionary<string, RelationPolicy>> ReadRelationsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT n.nspname, c.relname, c.relrowsecurity, c.relforcerowsecurity,
                   p.polname, pg_get_expr(p.polqual, p.polrelid), pg_get_expr(p.polwithcheck, p.polrelid),
                   p.polcmd::text, p.polpermissive, p.polroles = ARRAY[0::oid]
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_policy p ON p.polrelid = c.oid
            WHERE c.relkind IN ('r', 'p', 'v', 'm', 'f')
              AND {PostgresSchemaContract.UserSchemaPredicate}
            ORDER BY n.nspname, c.relname, p.polname;
            """;
        Dictionary<string, RelationPolicy> relations = new(StringComparer.Ordinal);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string key = $"{reader.GetString(0)}.{reader.GetString(1)}";
            if (!relations.TryGetValue(key, out RelationPolicy? relation))
            {
                relation = new(reader.GetBoolean(2), reader.GetBoolean(3), []);
                relations.Add(key, relation);
            }
            if (!reader.IsDBNull(4))
                relation.Policies.Add(new(
                    reader.GetString(4),
                    reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                    reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                    reader.GetString(7), reader.GetBoolean(8), reader.GetBoolean(9)));
        }
        return relations;
    }

    private static async Task<IReadOnlyList<SequenceInfo>> ReadSequencesAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new($"""
            SELECT n.nspname || '.' || c.relname,
                   CASE WHEN target.oid IS NULL THEN NULL ELSE target_namespace.nspname || '.' || target.relname END
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_depend dependency ON dependency.classid = 'pg_class'::regclass
              AND dependency.objid = c.oid AND dependency.deptype IN ('a', 'i')
            LEFT JOIN pg_class target ON target.oid = dependency.refobjid
            LEFT JOIN pg_namespace target_namespace ON target_namespace.oid = target.relnamespace
            WHERE c.relkind = 'S'
              AND {PostgresSchemaContract.UserSchemaPredicate}
            ORDER BY 1
            """, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        List<SequenceInfo> result = [];
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        return result;
    }

    private static async Task<IReadOnlyList<string>> ReadUserFunctionsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new($"""
            SELECT n.nspname || '.' || p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ')'
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE {PostgresSchemaContract.UserSchemaPredicate}
            ORDER BY 1
            """, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        List<string> result = [];
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
        return result;
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

    private static bool EnforcesOrganization(string expression) =>
        NormalizePolicy(expression) == NormalizePolicy(
            "\"OrganizationId\" = NULLIF(current_setting('app.organization_id'::text, true), ''::text)::uuid");

    // Compare the complete catalog expression, not tokens occurring somewhere
    // inside it. PostgreSQL adds harmless parentheses when deparsing an AST.
    private static string NormalizePolicy(string expression, bool preserveParentheses = false)
    {
        System.Text.StringBuilder result = new();
        char quote = '\0';
        foreach (char character in expression)
        {
            if (quote != '\0')
            {
                result.Append(character);
                if (character == quote) quote = '\0';
            }
            else if (character is '\'' or '"')
            {
                quote = character;
                result.Append(character);
            }
            else if (!char.IsWhiteSpace(character) && (preserveParentheses || character is not '(' and not ')')) result.Append(character);
        }
        return result.ToString();
    }

    private sealed record RelationPolicy(bool RowSecurity, bool ForceRowSecurity, List<Policy> Policies);
    private sealed record SequenceInfo(string Relation, string? OwnedByRelation);
    private sealed record Policy(string Name, string Using, string WithCheck, string Command, bool Permissive, bool AppliesToPublic);
}
