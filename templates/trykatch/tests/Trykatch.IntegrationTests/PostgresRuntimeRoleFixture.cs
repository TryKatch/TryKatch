using Npgsql;

namespace Trykatch.IntegrationTests;

internal static class PostgresRuntimeRoleFixture
{
    public const string OrganizationRole = "trykatch_org_runtime";
    public const string OrganizationPassword = "organization-runtime-test-password";
    public const string PlatformRole = "trykatch_platform_runtime";
    public const string PlatformPassword = "platform-runtime-test-password";
    public const string IdentityRole = "trykatch_identity_runtime";
    public const string IdentityPassword = "identity-runtime-test-password";
    public const string OutboxRole = "trykatch_outbox_worker";
    public const string OutboxPassword = "outbox-runtime-test-password";

    public static async Task EnsureRuntimeRolesAsync(string connectionString)
    {
        NpgsqlConnectionStringBuilder maintenanceConnection = new(connectionString)
        {
            Database = "postgres",
            Pooling = false
        };
        await using NpgsqlConnection connection = new(maintenanceConnection.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT pg_advisory_xact_lock(hashtextextended('trykatch.integration.policy-roles', 20260910));
            DO $policy_roles$
            BEGIN
              IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'trykatch_org_runtime') THEN
                CREATE ROLE "trykatch_org_runtime";
              END IF;
              IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'trykatch_platform_runtime') THEN
                CREATE ROLE "trykatch_platform_runtime";
              END IF;
              IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'trykatch_identity_runtime') THEN
                CREATE ROLE "trykatch_identity_runtime";
              END IF;
              IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'trykatch_outbox_worker') THEN
                CREATE ROLE "trykatch_outbox_worker";
              END IF;

              ALTER ROLE "trykatch_org_runtime" LOGIN PASSWORD 'organization-runtime-test-password'
                VALID UNTIL 'infinity' CONNECTION LIMIT -1
                NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
              ALTER ROLE "trykatch_platform_runtime" LOGIN PASSWORD 'platform-runtime-test-password'
                VALID UNTIL 'infinity' CONNECTION LIMIT -1
                NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
              ALTER ROLE "trykatch_identity_runtime" LOGIN PASSWORD 'identity-runtime-test-password'
                VALID UNTIL 'infinity' CONNECTION LIMIT -1
                NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
              ALTER ROLE "trykatch_outbox_worker" LOGIN PASSWORD 'outbox-runtime-test-password'
                VALID UNTIL 'infinity' CONNECTION LIMIT -1
                NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            END
            $policy_roles$;
            """;
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    public static async Task<(string Organization, string Platform, string Identity, string Outbox)> CreateConnectionStringsAsync(string ownerConnection)
    {
        await EnsureRuntimeRolesAsync(ownerConnection);
        return (
            ForRole(ownerConnection, OrganizationRole, OrganizationPassword),
            ForRole(ownerConnection, PlatformRole, PlatformPassword),
            ForRole(ownerConnection, IdentityRole, IdentityPassword),
            ForRole(ownerConnection, OutboxRole, OutboxPassword));
    }

    public static async Task GrantApplicationPrivilegesAsync(string ownerConnection)
    {
        await using NpgsqlConnection connection = new(ownerConnection);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = $"""
            REVOKE ALL ON SCHEMA public FROM PUBLIC;
            REVOKE ALL ON ALL TABLES IN SCHEMA public FROM PUBLIC;
            REVOKE ALL ON ALL FUNCTIONS IN SCHEMA public FROM PUBLIC;
            REVOKE TEMPORARY ON DATABASE {QuoteIdentifier(connection.Database)} FROM PUBLIC;
            GRANT CONNECT ON DATABASE {QuoteIdentifier(connection.Database)} TO {OrganizationRole}, {PlatformRole}, {IdentityRole}, {OutboxRole};

            GRANT USAGE ON SCHEMA app, platform TO {OrganizationRole};
            GRANT SELECT ON platform.organizations, platform.module_data_resources TO {OrganizationRole};
            GRANT SELECT, INSERT, UPDATE, DELETE ON platform.memberships, platform.roles,
                platform.membership_roles, platform.role_permissions, platform.invitations TO {OrganizationRole};
            GRANT SELECT, INSERT ON platform.audit_entries TO {OrganizationRole};
            GRANT INSERT ON platform.audit_intents TO {OrganizationRole};
            GRANT INSERT ON platform.outbox_messages TO {OrganizationRole};
            GRANT SELECT, INSERT, UPDATE, DELETE ON app.projects, app.documents TO {OrganizationRole};

            GRANT USAGE ON SCHEMA platform TO {PlatformRole};
            GRANT SELECT, INSERT, UPDATE, DELETE ON platform.organizations,
                platform.organization_data_placements, platform.organization_creation_intents TO {PlatformRole};
            GRANT SELECT, INSERT ON platform.roles, platform.role_permissions, platform.invitations TO {PlatformRole};

            GRANT USAGE ON SCHEMA identity TO {IdentityRole};
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA identity TO {IdentityRole};
            GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA identity TO {IdentityRole};

            GRANT USAGE ON SCHEMA platform TO {OutboxRole};
            GRANT SELECT, UPDATE ON platform.outbox_messages TO {OutboxRole};
            GRANT SELECT ON platform.audit_intents TO {OutboxRole};
            GRANT SELECT, INSERT ON platform.audit_entries TO {OutboxRole};

            GRANT EXECUTE ON FUNCTION platform.redact_legacy_outbox_error() TO {OrganizationRole}, {OutboxRole};
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static string ForRole(string ownerConnection, string role, string password) =>
        new NpgsqlConnectionStringBuilder(ownerConnection) { Username = role, Password = password }.ConnectionString;

    private static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
