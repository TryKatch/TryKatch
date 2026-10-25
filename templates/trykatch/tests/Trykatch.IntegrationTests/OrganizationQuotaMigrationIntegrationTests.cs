using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed partial class OrganizationQuotaMigrationIntegrationTests
{
    private const string Image = "postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f";
    private static readonly string[] Tables =
    [
        "organization_entitlements", "organization_quota_reservations", "organization_usage",
        "organization_quota_inventory", "module_upload_operations"
    ];
    private static readonly string[] Privileges = ["SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER"];
    private static readonly string[] Roles =
    [
        PostgresRuntimeRoleFixture.OrganizationRole, PostgresRuntimeRoleFixture.PlatformRole,
        PostgresRuntimeRoleFixture.IdentityRole, PostgresRuntimeRoleFixture.OutboxRole,
        PostgresRuntimeRoleFixture.WorkCoordinatorRole
    ];

    [TestMethod]
    public async Task MigratorCreatesForcedRlsQuotaRelationsWithOnlyExactPrivilegesIdempotently()
    {
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder(Image).Build();
        await postgres.StartAsync();
        string ownerConnection = postgres.GetConnectionString();
        await PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(ownerConnection);

        var first = await ModuleJobMigrationIntegrationTests.InvokeMigratorAsync(ownerConnection);
        first.Exit.ShouldBe(0, first.Error + first.Output);
        var repeated = await ModuleJobMigrationIntegrationTests.InvokeMigratorAsync(ownerConnection);
        repeated.Exit.ShouldBe(0, repeated.Error + repeated.Output);

        await using NpgsqlConnection owner = new(ownerConnection);
        await owner.OpenAsync();
        foreach (string table in Tables.Append("entitlement_publication_history"))
        {
            string relation = table == "entitlement_publication_history" ? $"platform.{table}" : $"app.{table}";
            await using (NpgsqlCommand command = new("""
                SELECT relrowsecurity, relforcerowsecurity FROM pg_class WHERE oid = to_regclass(@relation)
                """, owner))
            {
                command.Parameters.AddWithValue("relation", relation);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
                (await reader.ReadAsync()).ShouldBeTrue($"Expected {relation} after the forward migration.");
                reader.GetBoolean(0).ShouldBeTrue();
                reader.GetBoolean(1).ShouldBeTrue();
            }

            foreach (string role in Roles)
            foreach (string privilege in Privileges)
            {
                bool expected = table == "entitlement_publication_history"
                    ? role == PostgresRuntimeRoleFixture.PlatformRole && privilege is "SELECT" or "INSERT"
                    : role == PostgresRuntimeRoleFixture.OrganizationRole
                    ? table switch
                    {
                        "organization_entitlements" => privilege == "SELECT",
                        "organization_usage" => privilege is "SELECT" or "INSERT",
                        _ => privilege is "SELECT" or "INSERT" or "UPDATE"
                    }
                    : role == PostgresRuntimeRoleFixture.PlatformRole && table == "organization_entitlements"
                        && privilege is "SELECT" or "INSERT" or "UPDATE";
                await using NpgsqlCommand grant = new("SELECT has_table_privilege(@role, @relation, @privilege)", owner);
                grant.Parameters.AddWithValue("role", role);
                grant.Parameters.AddWithValue("relation", relation);
                grant.Parameters.AddWithValue("privilege", privilege);
                (await grant.ExecuteScalarAsync()).ShouldBe(expected, $"Unexpected {privilege} for {role} on {relation}.");
            }
        }
    }
}
