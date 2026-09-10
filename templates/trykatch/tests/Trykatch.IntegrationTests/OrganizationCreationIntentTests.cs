using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Application.Authorization;
using Trykatch.Application.Common;
using Trykatch.Application.Organizations;
using Trykatch.Domain.Organizations;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class OrganizationCreationIntentTests
{
    [TestMethod]
    public async Task LegacyOrganizationWithoutCreationIntentCannotBeClaimedThroughRetry()
    {
        await using CreationDatabase database = await CreationDatabase.CreateAsync();
        Guid actorId = Guid.CreateVersion7();
        await database.SeedLegacyOrganizationAsync("legacy-workspace");
        RecordingPlacementAdapter adapter = new();

        Result<CreateOrganizationResult> result = await database.CreateAsync(
            adapter,
            new("Legacy", "legacy-workspace", "attacker@example.test", actorId));

        result.IsSuccess.ShouldBeFalse();
        result.ErrorCode.ShouldBe("slug_conflict");
        adapter.Attempts.ShouldBe(0);
        (await database.CountAsync("platform.organization_creation_intents")).ShouldBe(0);
        (await database.CountAsync("platform.invitations")).ShouldBe(0);
    }

    [TestMethod]
    public async Task FailedCreationCanOnlyRetryWithItsOriginalActorAndAdministrator()
    {
        await using CreationDatabase database = await CreationDatabase.CreateAsync();
        Guid actorId = Guid.CreateVersion7();
        RecordingPlacementAdapter adapter = new(failFirstAttempt: true);
        CreateOrganizationCommand original = new(
            "Acme",
            "acme-workspace",
            "owner@example.test",
            actorId);

        Result<CreateOrganizationResult> failed = await database.CreateAsync(adapter, original);
        Result<CreateOrganizationResult> changedEmail = await database.CreateAsync(
            adapter,
            original with { AdministratorEmail = "different@example.test" });
        Result<CreateOrganizationResult> changedActor = await database.CreateAsync(
            adapter,
            original with { InitiatingActorId = Guid.CreateVersion7() });
        Result<CreateOrganizationResult> retried = await database.CreateAsync(adapter, original);

        failed.ErrorCode.ShouldBe("provisioning_failed");
        changedEmail.ErrorCode.ShouldBe("slug_conflict");
        changedActor.ErrorCode.ShouldBe("slug_conflict");
        retried.IsSuccess.ShouldBeTrue();
        retried.Value!.AdministratorEmail.ShouldBe("owner@example.test");
        adapter.Attempts.ShouldBe(2);
        (await database.CountAsync("platform.invitations")).ShouldBe(1);
    }

    [TestMethod]
    public async Task ConcurrentRetriesShareOnePlacementAndOneInvitationResult()
    {
        await using CreationDatabase database = await CreationDatabase.CreateAsync();
        Guid actorId = Guid.CreateVersion7();
        CreateOrganizationCommand command = new(
            "Concurrent",
            "concurrent-workspace",
            "owner@example.test",
            actorId);
        RecordingPlacementAdapter adapter = new(failFirstAttempt: true, delay: TimeSpan.FromMilliseconds(100));
        (await database.CreateAsync(adapter, command)).IsSuccess.ShouldBeFalse();

        Result<CreateOrganizationResult>[] retries = await Task.WhenAll(
            database.CreateAsync(adapter, command),
            database.CreateAsync(adapter, command));

        retries.ShouldAllBe(result => result.IsSuccess);
        retries[0].Value!.InvitationToken.ShouldBe(retries[1].Value!.InvitationToken);
        adapter.Attempts.ShouldBe(2);
        (await database.CountAsync("platform.invitations")).ShouldBe(1);
        (await database.CountAsync("platform.organization_creation_intents", "\"CompletedAt\" IS NOT NULL")).ShouldBe(1);
    }

    private sealed class CreationDatabase : IAsyncDisposable
    {
        private readonly PostgreSqlContainer? container;
        private readonly string administratorConnection;
        private readonly string databaseName;
        private readonly string connectionString;
        private readonly IPermissionCatalog permissions = new PermissionCatalog([new BuiltInPermissionDefinitionProvider()]);

        private CreationDatabase(PostgreSqlContainer? container, string administratorConnection)
        {
            this.container = container;
            this.administratorConnection = administratorConnection;
            databaseName = $"creation_{Guid.NewGuid():N}";
            connectionString = new Npgsql.NpgsqlConnectionStringBuilder(administratorConnection)
            {
                Database = databaseName,
                Pooling = false
            }.ConnectionString;
        }

        public static async Task<CreationDatabase> CreateAsync()
        {
            string? administrator = Environment.GetEnvironmentVariable("TRYKATCH_TEST_POSTGRES");
            PostgreSqlContainer? container = null;
            if (string.IsNullOrWhiteSpace(administrator))
            {
                container = new PostgreSqlBuilder(
                    "postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f").Build();
                await container.StartAsync();
                administrator = container.GetConnectionString();
            }
            CreationDatabase result = new(container, administrator);
            await result.ExecuteAsync($"CREATE DATABASE {result.databaseName}", administrator);
            await PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(result.connectionString);
            await using PlatformDbContext migration = result.CreateContext();
            await migration.Database.MigrateAsync();
            return result;
        }

        public async Task<Result<CreateOrganizationResult>> CreateAsync(
            RecordingPlacementAdapter adapter,
            CreateOrganizationCommand command)
        {
            await using PlatformDbContext context = CreateContext();
            await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync();
            OrganizationDirectory directory = new(context, permissions, TimeProvider.System);
            OrganizationDataPlacement placement = new(context, [adapter], TimeProvider.System);
            CreateOrganization handler = new(
                directory,
                placement,
                new TestTokenProtector(),
                TimeProvider.System,
                new CreateOrganizationValidator());
            Result<CreateOrganizationResult> result = await handler.HandleAsync(command, CancellationToken.None);
            await transaction.CommitAsync();
            return result;
        }

        public async Task SeedLegacyOrganizationAsync(string slug)
        {
            await using PlatformDbContext context = CreateContext();
            await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync();
            OrganizationDirectory directory = new(context, permissions, TimeProvider.System);
            Organization organization = Organization.Create("Legacy", slug);
            await directory.AddAsync(organization, CancellationToken.None);
            await directory.SeedRolesAsync(organization.Id, CancellationToken.None);
            await directory.SaveChangesAsync(CancellationToken.None);
            await transaction.CommitAsync();
        }

        public async Task<int> CountAsync(string relation, string? predicate = null)
        {
            await using Npgsql.NpgsqlConnection connection = new(connectionString);
            await connection.OpenAsync();
            await using Npgsql.NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT count(*)::integer FROM {relation}{(predicate is null ? string.Empty : $" WHERE {predicate}")}";
            return (int)(await command.ExecuteScalarAsync() ?? 0);
        }

        private PlatformDbContext CreateContext() => new(
            new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(connectionString).Options);

        private async Task ExecuteAsync(string sql, string? connection = null)
        {
            await using Npgsql.NpgsqlConnection database = new(connection ?? connectionString);
            await database.OpenAsync();
            await using Npgsql.NpgsqlCommand command = new(sql, database);
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await ExecuteAsync($"DROP DATABASE {databaseName} WITH (FORCE)", administratorConnection);
            }
            finally
            {
                if (container is not null) await container.DisposeAsync();
            }
        }
    }

    private sealed class RecordingPlacementAdapter(bool failFirstAttempt = false, TimeSpan? delay = null)
        : IOrganizationDataPlacementAdapter
    {
        private int attempts;
        public int Attempts => attempts;
        public OrganizationDataPlacementKind Kind => OrganizationDataPlacementKind.Shared;

        public async Task<OrganizationDataRoute> ProvisionAsync(
            OrganizationDataPlacementRequest request,
            CancellationToken cancellationToken)
        {
            int attempt = Interlocked.Increment(ref attempts);
            if (delay is not null) await Task.Delay(delay.Value, cancellationToken);
            if (failFirstAttempt && attempt == 1) throw new InvalidOperationException("simulated failure");
            return new(request.OrganizationId, Kind, "postgres", "test", "test", "1.0.0");
        }
    }

    private sealed class TestTokenProtector : IOrganizationInvitationTokenProtector
    {
        public string Protect(string token) => $"protected:{token}";
        public string Unprotect(string protectedToken) => protectedToken["protected:".Length..];
    }
}
