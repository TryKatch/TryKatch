using Npgsql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Application.Mailing;
using Trykatch.Identity;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;
using Trykatch.Modules.Documents.Infrastructure;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class QueuedEmailMigrationIntegrationTests
{
    [TestMethod]
    public async Task ExistingE4DatabaseUpgradesWithoutLosingIdentityRecords()
    {
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder(
            "postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f").Build();
        await postgres.StartAsync();
        string connection = postgres.GetConnectionString();
        await PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(connection);
        Guid userId = Guid.NewGuid();
        await using (IdentityDbContext identity = new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(connection).Options))
        {
            await identity.GetService<IMigrator>().MigrateAsync("20260911151243_AddAccountSecurityStepUp");
            identity.Users.Add(new() { Id = userId, UserName = "existing-user", DisplayName = "Existing user" });
            await identity.SaveChangesAsync();
        }
        await using (PlatformDbContext platform = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(connection).Options))
            await platform.Database.MigrateAsync();
        await using (ApplicationDbContext application = new(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connection).Options,
            [new ProjectsModelContributor(), new DocumentsModelContributor()],
            moduleCatalog: new ModuleCatalog([new ProjectsModule(), new DocumentsModule()])))
            await application.GetService<IMigrator>().MigrateAsync("20261023221455_AddDurableModuleJobs");

        (int exit, string output, string error) = await ModuleJobMigrationIntegrationTests.InvokeMigratorAsync(connection);
        exit.ShouldBe(0, $"Existing E4 migration failed.{Environment.NewLine}{error}{Environment.NewLine}{output}");
        await using IdentityDbContext upgraded = new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(connection).Options);
        (await upgraded.Users.SingleAsync(user => user.Id == userId)).DisplayName.ShouldBe("Existing user");
        await AssertIdentityStoreUsesItsNarrowRuntimeRoleAsync(connection);
    }

    [TestMethod]
    public async Task MigratorAddsOwnedEmailStoresAndExactRuntimePrivileges()
    {
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder(
            "postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f").Build();
        await postgres.StartAsync();
        string connectionString = postgres.GetConnectionString();
        await PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(connectionString);
        foreach (string stage in new[] { "fresh", "existing" })
        {
            (int exit, string output, string error) = await ModuleJobMigrationIntegrationTests.InvokeMigratorAsync(connectionString);
            exit.ShouldBe(0, $"Email migration ({stage}) failed.{Environment.NewLine}{error}{Environment.NewLine}{output}");
        }

        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        string[] roles = [PostgresRuntimeRoleFixture.OrganizationRole, PostgresRuntimeRoleFixture.PlatformRole,
            PostgresRuntimeRoleFixture.IdentityRole, PostgresRuntimeRoleFixture.OutboxRole,
            PostgresRuntimeRoleFixture.WorkCoordinatorRole];
        foreach (string relation in new[] { "identity.email_deliveries", "infrastructure.email_deliveries" })
        foreach (string role in roles)
        foreach (string permission in new[] { "SELECT", "INSERT", "UPDATE", "DELETE" })
        {
            bool expected = (relation, role) switch
            {
                ("identity.email_deliveries", PostgresRuntimeRoleFixture.IdentityRole) => permission != "DELETE",
                ("infrastructure.email_deliveries", PostgresRuntimeRoleFixture.OrganizationRole) => permission == "INSERT",
                ("infrastructure.email_deliveries", PostgresRuntimeRoleFixture.WorkCoordinatorRole) => permission is "SELECT" or "UPDATE",
                _ => false
            };
            await using NpgsqlCommand access = new("SELECT has_table_privilege(@role, @relation, @permission)", connection);
            access.Parameters.AddWithValue("role", role);
            access.Parameters.AddWithValue("relation", relation);
            access.Parameters.AddWithValue("permission", permission);
            ((bool)(await access.ExecuteScalarAsync())!).ShouldBe(expected, $"{role} {permission} {relation}");
        }

        await using NpgsqlCommand rls = new("""
            SELECT relrowsecurity AND relforcerowsecurity
            FROM pg_class WHERE oid = 'infrastructure.email_deliveries'::regclass
            """, connection);
        ((bool)(await rls.ExecuteScalarAsync())!).ShouldBeTrue();

        await AssertScopedInsertAndRollbackAsync(connectionString);
        await AssertIdentityStoreUsesItsNarrowRuntimeRoleAsync(connectionString);
    }

    private static async Task AssertIdentityStoreUsesItsNarrowRuntimeRoleAsync(string ownerConnection)
    {
        string runtime = new NpgsqlConnectionStringBuilder(ownerConnection)
        {
            Username = PostgresRuntimeRoleFixture.IdentityRole,
            Password = PostgresRuntimeRoleFixture.IdentityPassword
        }.ConnectionString;
        IdentityEmailDeliveryStore store = new(new DbContextOptionsBuilder<IdentityEmailDeliveryDbContext>()
            .UseNpgsql(runtime).Options, TimeProvider.System);
        Guid deliveryId = Guid.NewGuid();
        (await store.TryInsertAsync(new()
        {
            Id = deliveryId, UserId = Guid.NewGuid(), TemplateKey = "core.password-reset", TemplateVersion = 1,
            ProtectedIntent = "protected-fixture", State = "queued"
        }, CancellationToken.None)).ShouldBeTrue();
        IReadOnlyList<EmailDeliveryLease> leases = await store.ClaimAsync(1, CancellationToken.None);
        leases.Count.ShouldBe(1);
        leases[0].Context.DeliveryId.ShouldBe(deliveryId);
        (await store.CompleteAsync(leases[0], new(EmailAttemptDisposition.Acknowledged), CancellationToken.None))
            .ShouldBeTrue();
        (await store.FindAsync(deliveryId, CancellationToken.None))!.State.ShouldBe("delivered");
    }

    private static async Task AssertScopedInsertAndRollbackAsync(string ownerConnection)
    {
        string runtime = new NpgsqlConnectionStringBuilder(ownerConnection)
        {
            Username = PostgresRuntimeRoleFixture.OrganizationRole,
            Password = PostgresRuntimeRoleFixture.OrganizationPassword
        }.ConnectionString;
        await using NpgsqlConnection connection = new(runtime);
        await connection.OpenAsync();
        Guid organization = Guid.NewGuid();
        Guid actor = Guid.NewGuid();
        Guid id = Guid.NewGuid();
        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync())
        {
            await using NpgsqlCommand scope = new("SELECT set_config('app.organization_id', @organization, true), set_config('app.actor_id', @actor, true)", connection, transaction);
            scope.Parameters.AddWithValue("organization", organization.ToString());
            scope.Parameters.AddWithValue("actor", actor.ToString());
            await scope.ExecuteNonQueryAsync();
            await InsertAsync(connection, transaction, id, organization, actor);
            await transaction.RollbackAsync();
        }
        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync())
        {
            PostgresException denied = await Should.ThrowAsync<PostgresException>(() =>
                InsertAsync(connection, transaction, id, organization, actor));
            denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
            await transaction.RollbackAsync();
        }
        foreach ((Guid rowOrganization, Guid rowActor) in new[] { (Guid.NewGuid(), actor), (organization, Guid.NewGuid()) })
        {
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
            await using NpgsqlCommand scope = new("SELECT set_config('app.organization_id', @organization, true), set_config('app.actor_id', @actor, true)", connection, transaction);
            scope.Parameters.AddWithValue("organization", organization.ToString());
            scope.Parameters.AddWithValue("actor", actor.ToString());
            await scope.ExecuteNonQueryAsync();
            PostgresException denied = await Should.ThrowAsync<PostgresException>(() =>
                InsertAsync(connection, transaction, id, rowOrganization, rowActor));
            denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
            await transaction.RollbackAsync();
        }
        await using NpgsqlConnection owner = new(ownerConnection);
        await owner.OpenAsync();
        await using NpgsqlCommand count = new("SELECT count(*) FROM infrastructure.email_deliveries WHERE \"Id\" = @id", owner);
        count.Parameters.AddWithValue("id", id);
        ((long)(await count.ExecuteScalarAsync())!).ShouldBe(0);
    }

    private static async Task InsertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid id, Guid organization, Guid actor)
    {
        await using NpgsqlCommand insert = new("""
            INSERT INTO infrastructure.email_deliveries
                ("Id", "OrganizationId", "ActorId", "MessageId", "AdmissionIdentityKey", "TemplateKey",
                 "TemplateVersion", "ProtectedIntent", "State", "Attempts", "CreatedAtUtc")
            VALUES (@id, @organization, @actor, @message, 'user-command:fixture', 'fixture.notice', 1,
                    'protected-fixture', 'queued', 0, CURRENT_TIMESTAMP)
            """, connection, transaction);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("organization", organization);
        insert.Parameters.AddWithValue("actor", actor);
        insert.Parameters.AddWithValue("message", Guid.NewGuid());
        await insert.ExecuteNonQueryAsync();
    }
}
