using System.Diagnostics;
using System.Reflection;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Infrastructure.Jobs;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class ModuleJobMigrationIntegrationTests
{
    [TestMethod]
    public async Task MigratorAppliesDurableJobSchemaAndRuntimeProfilesOnFreshAndExistingDatabase()
    {
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder(
            "postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f").Build();
        await postgres.StartAsync();
        string connectionString = postgres.GetConnectionString();
        await PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(connectionString);

        (int Exit, string Output, string Error) first = await InvokeMigratorAsync(connectionString);
        first.Exit.ShouldBe(0, $"Fresh database migration failed.{Environment.NewLine}{first.Error}{Environment.NewLine}{first.Output}");

        (int Exit, string Output, string Error) second = await InvokeMigratorAsync(connectionString);
        second.Exit.ShouldBe(0, $"Existing database migration failed.{Environment.NewLine}{second.Error}{Environment.NewLine}{second.Output}");

        await AssertQuartzSchemaAndPrivilegesAsync(connectionString);
    }

    internal static async Task<(int Exit, string Output, string Error)> InvokeMigratorAsync(string connectionString)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Trykatch.slnx"))) root = root.Parent;
        DirectoryInfo projectRoot = root ?? throw new InvalidOperationException("Could not locate Trykatch.slnx from the integration test output directory.");

        string configuration = typeof(ModuleJobMigrationIntegrationTests).Assembly
            .GetCustomAttributes(typeof(AssemblyConfigurationAttribute), false)
            .Cast<AssemblyConfigurationAttribute>()
            .Single()
            .Configuration;
        string migratorPath = Path.Combine(projectRoot.FullName,
            $"src/API/Trykatch.Migrator/bin/{configuration}/net10.0/Trykatch.Migrator.dll");
        File.Exists(migratorPath).ShouldBeTrue($"Migrator output was not found at {migratorPath}.");

        string dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? string.Empty;
        string dotnet = Path.Combine(dotnetRoot, "dotnet");
        if (string.IsNullOrWhiteSpace(dotnetRoot) || !File.Exists(dotnet)) dotnet = "dotnet";

        ProcessStartInfo start = new(dotnet)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = projectRoot.FullName
        };
        start.ArgumentList.Add(migratorPath);
        start.Environment["ConnectionStrings__trykatchdb"] = connectionString;
        start.Environment["Database__OrganizationRuntimeRole"] = PostgresRuntimeRoleFixture.OrganizationRole;
        start.Environment["Database__PlatformRuntimeRole"] = PostgresRuntimeRoleFixture.PlatformRole;
        start.Environment["Database__IdentityRuntimeRole"] = PostgresRuntimeRoleFixture.IdentityRole;
        start.Environment["Database__OutboxWorkerRole"] = PostgresRuntimeRoleFixture.OutboxRole;
        start.Environment["Database__WorkCoordinatorRole"] = PostgresRuntimeRoleFixture.WorkCoordinatorRole;

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the migrator process.");
        return await ProcessExecution.WaitAsync(process, TimeSpan.FromSeconds(30));
    }

    private static async Task AssertQuartzSchemaAndPrivilegesAsync(string connectionString)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("""
            SELECT c.relname, c.relrowsecurity, c.relforcerowsecurity,
              has_table_privilege('trykatch_work_coordinator', c.oid, 'SELECT'),
              has_table_privilege('trykatch_work_coordinator', c.oid, 'INSERT'),
              has_table_privilege('trykatch_work_coordinator', c.oid, 'UPDATE'),
              has_table_privilege('trykatch_work_coordinator', c.oid, 'DELETE'),
              has_table_privilege('trykatch_org_runtime', c.oid, 'SELECT'),
              has_table_privilege('trykatch_org_runtime', c.oid, 'INSERT'),
              has_table_privilege('trykatch_org_runtime', c.oid, 'UPDATE'),
              has_table_privilege('trykatch_org_runtime', c.oid, 'DELETE'),
              has_table_privilege('trykatch_platform_runtime', c.oid, 'SELECT'),
              has_table_privilege('trykatch_platform_runtime', c.oid, 'INSERT'),
              has_table_privilege('trykatch_platform_runtime', c.oid, 'UPDATE'),
              has_table_privilege('trykatch_platform_runtime', c.oid, 'DELETE'),
              has_table_privilege('trykatch_identity_runtime', c.oid, 'SELECT'),
              has_table_privilege('trykatch_identity_runtime', c.oid, 'INSERT'),
              has_table_privilege('trykatch_identity_runtime', c.oid, 'UPDATE'),
              has_table_privilege('trykatch_identity_runtime', c.oid, 'DELETE'),
              has_table_privilege('trykatch_outbox_worker', c.oid, 'SELECT'),
              has_table_privilege('trykatch_outbox_worker', c.oid, 'INSERT'),
              has_table_privilege('trykatch_outbox_worker', c.oid, 'UPDATE'),
              has_table_privilege('trykatch_outbox_worker', c.oid, 'DELETE')
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'infrastructure' AND c.relname = ANY(@tables)
            ORDER BY c.relname
            """, connection);
        command.Parameters.AddWithValue("tables", QuartzSchemaContract.Tables);

        List<string> found = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            found.Add(reader.GetString(0));
            reader.GetBoolean(1).ShouldBeTrue();
            reader.GetBoolean(2).ShouldBeTrue();
            for (int privilege = 3; privilege <= 6; privilege++) reader.GetBoolean(privilege).ShouldBeTrue();
            for (int role = 7; role <= 22; role++) reader.GetBoolean(role).ShouldBeFalse();
        }

        found.OrderBy(table => table, StringComparer.Ordinal).ToArray()
            .ShouldBe(QuartzSchemaContract.Tables.OrderBy(table => table, StringComparer.Ordinal).ToArray());
    }
}
