using System.Diagnostics;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Api.Modules;
using Trykatch.Modules;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class ProductionMigratorTests
{
    private const string OwnerPassword = "migration-owner-fixture-password";
    private const string CatalogLock = "trykatch:installed-schema:v1";

    [TestMethod]
    public async Task ProductionPreflightRejectsMissingRolesAndUnsafeUsersBeforeCreatingHistory()
    {
        await using PostgreSqlContainer postgres = CreateDatabase();
        await postgres.StartAsync();
        string admin = postgres.GetConnectionString();
        await PrepareOwnerAsync(admin);
        string owner = OwnerConnection(admin, "preflight");
        var missing = await ModuleJobMigrationIntegrationTests.InvokeMigratorAsync(owner, start =>
        {
            Production(start);
            foreach (string key in start.Environment.Keys.Where(key => key.StartsWith("Database__", StringComparison.OrdinalIgnoreCase)).ToArray())
                start.Environment.Remove(key);
        });
        missing.Exit.ShouldNotBe(0);
        await AssertUntouchedAsync(admin);

        var privileged = await ModuleJobMigrationIntegrationTests.InvokeMigratorAsync(admin, Production);
        privileged.Exit.ShouldNotBe(0);
        await AssertUntouchedAsync(admin);

        await using NpgsqlConnection database = new(admin);
        await database.OpenAsync();
        await using NpgsqlCommand makeUnsafe = new("ALTER ROLE trykatch_identity_runtime CREATEROLE", database);
        await makeUnsafe.ExecuteNonQueryAsync();
        var unsafeRuntime = await ModuleJobMigrationIntegrationTests.InvokeMigratorAsync(owner, Production);
        unsafeRuntime.Exit.ShouldNotBe(0);
        await AssertUntouchedAsync(admin);
    }

    [TestMethod]
    public async Task SecondProductionDeploymentWaitsForFirstThroughCatalogSynchronization()
    {
        await using PostgreSqlContainer postgres = CreateDatabase();
        await postgres.StartAsync();
        string admin = postgres.GetConnectionString();
        await PrepareOwnerAsync(admin);

        await using NpgsqlConnection observer = new(admin);
        await observer.OpenAsync();
        await using NpgsqlTransaction barrier = await observer.BeginTransactionAsync();
        await using (NpgsqlCommand hold = new("SELECT pg_advisory_xact_lock(hashtext(@name))", observer, barrier))
        {
            hold.Parameters.AddWithValue("name", CatalogLock);
            await hold.ExecuteNonQueryAsync();
        }
        var first = ModuleJobMigrationIntegrationTests.InvokeMigratorAsync(OwnerConnection(admin, "first-deployment"), Production);
        var firstPid = await WaitForBlockedProcessAsync(observer, "first-deployment", observer.ProcessID);
        var second = ModuleJobMigrationIntegrationTests.InvokeMigratorAsync(OwnerConnection(admin, "second-deployment"), Production);
        await WaitForBlockedProcessAsync(observer, "second-deployment", firstPid);
        await barrier.CommitAsync();

        foreach (var result in await Task.WhenAll(first, second))
            result.Exit.ShouldBe(0, result.Error + result.Output);
        await using NpgsqlCommand ledger = new("SELECT count(*) FROM platform.module_migrations", observer);
        long expected = EnabledModules.All.OfType<IModuleMigrationContributor>().Sum(module => (long)module.Migrations.Count);
        ((long)(await ledger.ExecuteScalarAsync())!).ShouldBe(expected);
    }

    private static PostgreSqlContainer CreateDatabase() => new PostgreSqlBuilder(
        "postgres:19.0-alpine3.23@sha256:40ad7e457013448b5522c6c5d30dccb8248959c41adc0e56c1785be4293bbd67")
        .WithDatabase("deployment_test").Build();

    private static void Production(ProcessStartInfo start) => start.Environment["DOTNET_ENVIRONMENT"] = "Production";

    private static string OwnerConnection(string admin, string application) => new NpgsqlConnectionStringBuilder(admin)
    {
        Username = "trykatch_migrator",
        Password = OwnerPassword,
        ApplicationName = application
    }.ConnectionString;

    private static async Task PrepareOwnerAsync(string admin)
    {
        await PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(admin);
        await using NpgsqlConnection connection = new(admin);
        await connection.OpenAsync();
        await using NpgsqlCommand bootstrap = new(
            $"CREATE ROLE trykatch_migrator LOGIN NOINHERIT PASSWORD '{OwnerPassword}'; ALTER DATABASE deployment_test OWNER TO trykatch_migrator",
            connection);
        await bootstrap.ExecuteNonQueryAsync();
    }

    private static async Task AssertUntouchedAsync(string admin)
    {
        await using NpgsqlConnection connection = new(admin);
        await connection.OpenAsync();
        await using NpgsqlCommand probe = new("""
            SELECT to_regclass('public."__EFMigrationsHistory"') IS NULL AND NOT EXISTS (
                SELECT 1 FROM pg_namespace WHERE nspname IN ('app', 'platform', 'identity', 'infrastructure'))
            """, connection);
        (await probe.ExecuteScalarAsync()).ShouldBe(true);
    }

    private static async Task<int> WaitForBlockedProcessAsync(NpgsqlConnection observer, string application, int blockingPid)
    {
        // PostgreSQL caches pg_stat_activity snapshots in a transaction. Refresh before each observation.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(18));
        while (true)
        {
            await using NpgsqlCommand refresh = new("SELECT pg_stat_clear_snapshot()", observer);
            await refresh.ExecuteNonQueryAsync(timeout.Token);
            await using NpgsqlCommand probe = new("""
                SELECT pid FROM pg_stat_activity WHERE application_name = @name
                    AND wait_event = 'advisory' AND @blocker = ANY(pg_blocking_pids(pid))
                """, observer);
            probe.Parameters.AddWithValue("name", application);
            probe.Parameters.AddWithValue("blocker", blockingPid);
            if (await probe.ExecuteScalarAsync(timeout.Token) is int pid)
                return pid;
            await Task.Delay(100, timeout.Token);
        }
    }
}
