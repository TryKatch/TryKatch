using Npgsql;

namespace TrykatchApp.IntegrationTests;

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
                CREATE ROLE trykatch_org_runtime;
              END IF;
              IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'trykatch_platform_runtime') THEN
                CREATE ROLE trykatch_platform_runtime;
              END IF;
              IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'trykatch_identity_runtime') THEN
                CREATE ROLE trykatch_identity_runtime;
              END IF;
              IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'trykatch_outbox_worker') THEN
                CREATE ROLE trykatch_outbox_worker;
              END IF;

              ALTER ROLE trykatch_org_runtime LOGIN PASSWORD 'organization-runtime-test-password'
                VALID UNTIL 'infinity' CONNECTION LIMIT -1
                NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
              ALTER ROLE trykatch_platform_runtime LOGIN PASSWORD 'platform-runtime-test-password'
                VALID UNTIL 'infinity' CONNECTION LIMIT -1
                NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
              ALTER ROLE trykatch_identity_runtime LOGIN PASSWORD 'identity-runtime-test-password'
                VALID UNTIL 'infinity' CONNECTION LIMIT -1
                NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
              ALTER ROLE trykatch_outbox_worker LOGIN PASSWORD 'outbox-runtime-test-password'
                VALID UNTIL 'infinity' CONNECTION LIMIT -1
                NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            END
            $policy_roles$;
            """;
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }
}
