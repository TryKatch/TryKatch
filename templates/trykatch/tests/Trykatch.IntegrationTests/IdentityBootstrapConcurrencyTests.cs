using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenIddict.Abstractions;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Application.Identity;
using Trykatch.Identity;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class IdentityBootstrapConcurrencyTests
{
    private const long IdentityAdministrationLock = 734001;
    private const string PostgreSqlImage = "postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f";

    [TestMethod]
    public async Task ConcurrentColdPlatformAdministratorSeedingCreatesEachRoleAndAdministratorOnce()
    {
        await using IdentitySeedFixture fixture = await IdentitySeedFixture.StartAsync();
        string email = $"platform-admin-{Guid.NewGuid():N}@trykatch.test";
        string password = "Concurrent-bootstrap-Secret-2026!";
        using ServiceProvider first = IdentitySeedFixture.CreateProvider(fixture.Configuration(
            bootstrapEmail: email, bootstrapPassword: password));
        using ServiceProvider second = IdentitySeedFixture.CreateProvider(fixture.Configuration(
            bootstrapEmail: email, bootstrapPassword: password));

        await RunBehindHeldIdentityAdministrationLockAsync(
            fixture.OwnerConnection,
            [
                () => IdentitySeeder.SeedPlatformAdministratorAsync(first, fixture.Configuration(email, password)),
                () => IdentitySeeder.SeedPlatformAdministratorAsync(second, fixture.Configuration(email, password))
            ],
            async () =>
            {
                await using IdentityDbContext owner = fixture.CreateOwnerContext();
                (await owner.Roles.CountAsync()).ShouldBe(0);
                (await owner.Users.CountAsync()).ShouldBe(0);
            });

        await using IdentityDbContext verification = fixture.CreateOwnerContext();
        string[] actualRoles = await verification.Roles.AsNoTracking().Select(role => role.Name!).ToArrayAsync();
        string[] expectedRoles = PlatformRoles.All.Select(role => role.Key).ToArray();
        actualRoles.OrderBy(value => value, StringComparer.Ordinal).ToArray()
            .ShouldBe(expectedRoles.OrderBy(value => value, StringComparer.Ordinal).ToArray());

        (await verification.Users.CountAsync()).ShouldBe(1);
        ApplicationUser administrator = await verification.Users.SingleAsync(user => user.Email == email);
        administrator.IsPlatformAdministrator.ShouldBeTrue();
        administrator.IsPlatformAccessSuspended.ShouldBeFalse();
        Guid administratorRoleId = await verification.Roles.Where(role => role.Name == PlatformRoles.Administrator)
            .Select(role => role.Id).SingleAsync();
        (await verification.Set<IdentityUserRole<Guid>>().CountAsync(assignment =>
            assignment.UserId == administrator.Id && assignment.RoleId == administratorRoleId)).ShouldBe(1);
        (await verification.Set<IdentityUserRole<Guid>>().CountAsync()).ShouldBe(1);
    }

    [TestMethod]
    public async Task ConcurrentOpenIddictClientSeedingPersistsOneClientAndPreservesItsOriginalSecret()
    {
        await using IdentitySeedFixture fixture = await IdentitySeedFixture.StartAsync();
        const string clientId = "bootstrap-concurrency-client";
        const string originalSecret = "Original-client-secret-42!";
        using ServiceProvider first = IdentitySeedFixture.CreateProvider(fixture.Configuration(clientId: clientId, clientSecret: originalSecret));
        using ServiceProvider second = IdentitySeedFixture.CreateProvider(fixture.Configuration(clientId: clientId, clientSecret: originalSecret));

        await RunBehindHeldIdentityAdministrationLockAsync(
            fixture.OwnerConnection,
            [
                () => IdentitySeeder.SeedOpenIddictClientsAsync(first, fixture.Configuration(clientId: clientId, clientSecret: originalSecret)),
                () => IdentitySeeder.SeedOpenIddictClientsAsync(second, fixture.Configuration(clientId: clientId, clientSecret: originalSecret))
            ],
            async () => (await CountClientAsync(fixture.OwnerConnection, clientId)).ShouldBe(0));

        (await CountClientAsync(fixture.OwnerConnection, clientId)).ShouldBe(1);

        using ServiceProvider rerun = IdentitySeedFixture.CreateProvider(fixture.Configuration(clientId: clientId, clientSecret: "Changed-client-secret-84!"));
        await IdentitySeeder.SeedOpenIddictClientsAsync(rerun,
            fixture.Configuration(clientId: clientId, clientSecret: "Changed-client-secret-84!"));
        (await CountClientAsync(fixture.OwnerConnection, clientId)).ShouldBe(1);

        using IServiceScope scope = first.CreateScope();
        IOpenIddictApplicationManager applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        object application = await applications.FindByClientIdAsync(clientId)
            ?? throw new InvalidOperationException("The concurrent bootstrap client was not persisted.");
        (await applications.ValidateClientSecretAsync(application, originalSecret)).ShouldBeTrue();
        (await applications.ValidateClientSecretAsync(application, "Changed-client-secret-84!")).ShouldBeFalse();
    }

    private static async Task RunBehindHeldIdentityAdministrationLockAsync(
        string ownerConnection,
        Func<Task>[] seeders,
        Func<Task> assertNoCommittedRowsWhileLocked)
    {
        await using NpgsqlConnection owner = new(ownerConnection);
        await owner.OpenAsync();
        await using NpgsqlTransaction transaction = await owner.BeginTransactionAsync();
        await using (NpgsqlCommand acquire = new($"SELECT pg_advisory_xact_lock({IdentityAdministrationLock})", owner, transaction))
            await acquire.ExecuteNonQueryAsync();

        Task[] tasks = [];
        Exception? lockAssertionFailure = null;
        try
        {
            tasks = [seeders[0](), seeders[1]()];
            await WaitUntilBothSeedersBlockAsync(ownerConnection, tasks, assertNoCommittedRowsWhileLocked);
        }
        catch (Exception exception)
        {
            lockAssertionFailure = exception;
        }
        finally
        {
            await transaction.RollbackAsync();
        }

        Exception? seedingFailure = null;
        try
        {
            if (tasks.Length > 0) await Task.WhenAll(tasks);
        }
        catch (Exception exception)
        {
            seedingFailure = exception;
        }

        if (lockAssertionFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(lockAssertionFailure).Throw();
        if (seedingFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(seedingFailure).Throw();
    }

    private static async Task WaitUntilBothSeedersBlockAsync(
        string ownerConnection,
        Task[] seeders,
        Func<Task> assertNoCommittedRowsWhileLocked)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        int blockedCount = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await assertNoCommittedRowsWhileLocked();
            if (seeders.Any(task => task.IsFaulted)) await Task.WhenAll(seeders);
            blockedCount = await CountLockWaitersAsync(ownerConnection);
            if (blockedCount == 2)
            {
                await assertNoCommittedRowsWhileLocked();
                return;
            }
            if (seeders.All(task => task.IsCompleted))
                throw new InvalidOperationException($"Both seeders completed while the owner held the identity lock; blocked waiters: {blockedCount}.");
            await Task.Delay(25);
        }

        await assertNoCommittedRowsWhileLocked();
        throw new InvalidOperationException($"Expected two seeders blocked on the held identity advisory lock; observed {blockedCount}.");
    }

    private static async Task<int> CountLockWaitersAsync(string ownerConnection)
    {
        await using NpgsqlConnection observer = new(ownerConnection);
        await observer.OpenAsync();
        await using NpgsqlCommand command = new("""
            SELECT count(*)
            FROM pg_locks
            WHERE locktype = 'advisory'
              AND NOT granted
              AND classid = 0::oid
              AND objid = @lock_key::oid
              AND objsubid = 1
            """, observer);
        command.Parameters.AddWithValue("lock_key", IdentityAdministrationLock);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<int> CountClientAsync(string ownerConnection, string clientId)
    {
        await using NpgsqlConnection owner = new(ownerConnection);
        await owner.OpenAsync();
        await using NpgsqlCommand command = new("""
            SELECT count(*) FROM identity."OpenIddictApplications" WHERE "ClientId" = @client_id
            """, owner);
        command.Parameters.AddWithValue("client_id", clientId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class IdentitySeedFixture : IAsyncDisposable
    {
        private readonly PostgreSqlContainer postgres;
        private readonly string runtimeConnection;

        private IdentitySeedFixture(PostgreSqlContainer postgres, string ownerConnection, string runtimeConnection)
        {
            this.postgres = postgres;
            OwnerConnection = ownerConnection;
            this.runtimeConnection = runtimeConnection;
        }

        public string OwnerConnection { get; }

        public static async Task<IdentitySeedFixture> StartAsync()
        {
            PostgreSqlContainer postgres = new PostgreSqlBuilder(PostgreSqlImage).Build();
            try
            {
                await postgres.StartAsync();
                string ownerConnection = postgres.GetConnectionString();
                await PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(ownerConnection);
                await using (IdentityDbContext identity = new(new DbContextOptionsBuilder<IdentityDbContext>()
                    .UseNpgsql(ownerConnection).Options))
                {
                    await identity.Database.MigrateAsync();
                }
                await GrantIdentitySchemaOnlyAsync(ownerConnection);
                string runtimeConnection = new NpgsqlConnectionStringBuilder(ownerConnection)
                {
                    Username = PostgresRuntimeRoleFixture.IdentityRole,
                    Password = PostgresRuntimeRoleFixture.IdentityPassword,
                    Pooling = false
                }.ConnectionString;
                return new IdentitySeedFixture(postgres, ownerConnection, runtimeConnection);
            }
            catch
            {
                await postgres.DisposeAsync();
                throw;
            }
        }

        public IdentityDbContext CreateOwnerContext() =>
            new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(OwnerConnection).Options);

        public static ServiceProvider CreateProvider(IConfiguration configuration)
        {
            ServiceCollection services = new();
            services.AddLogging();
            services.AddIdentity(configuration, isDevelopment: true);
            return services.BuildServiceProvider(validateScopes: true);
        }

        public IConfiguration Configuration(
            string? bootstrapEmail = null,
            string? bootstrapPassword = null,
            string? clientId = null,
            string? clientSecret = null) =>
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:trykatch-identity"] = runtimeConnection,
                ["Bootstrap:PlatformAdminEmail"] = bootstrapEmail,
                ["Bootstrap:PlatformAdminPassword"] = bootstrapPassword,
                ["OpenIddict:Clients:0:ClientId"] = clientId,
                ["OpenIddict:Clients:0:ClientSecret"] = clientSecret,
                ["OpenIddict:Clients:0:GrantType"] = "client_credentials",
                ["OpenIddict:Clients:0:DisplayName"] = "Concurrent bootstrap fixture"
            }).Build();

        public async ValueTask DisposeAsync() => await postgres.DisposeAsync();

        private static async Task GrantIdentitySchemaOnlyAsync(string ownerConnection)
        {
            await using NpgsqlConnection owner = new(ownerConnection);
            await owner.OpenAsync();
            string database = $"\"{owner.Database.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
            await using NpgsqlCommand grant = new($"""
                GRANT CONNECT ON DATABASE {database} TO "{PostgresRuntimeRoleFixture.IdentityRole}";
                GRANT USAGE ON SCHEMA identity TO "{PostgresRuntimeRoleFixture.IdentityRole}";
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA identity TO "{PostgresRuntimeRoleFixture.IdentityRole}";
                GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA identity TO "{PostgresRuntimeRoleFixture.IdentityRole}";
                """, owner);
            await grant.ExecuteNonQueryAsync();
        }
    }
}
