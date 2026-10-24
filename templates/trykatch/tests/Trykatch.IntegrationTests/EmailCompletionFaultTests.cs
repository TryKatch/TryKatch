#if TRYKATCH_EMAIL
using System.Data.Common;
using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Application.Mailing;
using Trykatch.Identity;
using Trykatch.Infrastructure.Mailing;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class EmailCompletionFaultTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task IdentityCompletionRecoversOnlyTheExactOutcomeAfterUpdateAcknowledgementIsLost()
    {
        await using PostgreSqlContainer postgres = await StartPostgresAsync();
        string ownerConnection = await PrepareMigratedDatabaseAsync(postgres);
        DbContextOptions<IdentityEmailDeliveryDbContext> ownerOptions = IdentityOptions(ownerConnection);
        Guid deliveryId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        await SeedIdentityAsync(ownerOptions, new IdentityEmailDeliveryRecord
        {
            Id = deliveryId,
            UserId = userId,
            TemplateKey = "core.password-reset",
            TemplateVersion = 1,
            ProtectedIntent = "protected-fixture",
            State = "queued",
            Attempts = 0,
            CreatedAtUtc = Now
        });

        FakeTimeProvider clock = new(Now);
        string runtimeConnection = RoleConnection(ownerConnection,
            PostgresRuntimeRoleFixture.IdentityRole, PostgresRuntimeRoleFixture.IdentityPassword);
        IdentityEmailDeliveryStore claimStore = new(IdentityOptions(runtimeConnection), clock);
        EmailDeliveryLease lease = (await claimStore.ClaimAsync(1, CancellationToken.None)).Single();
        lease.Context.DeliveryId.ShouldBe(deliveryId);
        lease.Context.BoundaryOwnerId.ShouldBe(userId);

        CompletionUpdateAcknowledgementFault fault = new("identity.email_deliveries");
        IdentityEmailDeliveryStore faultedStore = new(IdentityOptions(runtimeConnection, fault), clock);
        EmailAttemptOutcome acknowledged = new(EmailAttemptDisposition.Acknowledged);

        await Should.ThrowAsync<IOException>(() => faultedStore.CompleteAsync(lease, acknowledged, CancellationToken.None));

        fault.UpdateAttempts.ShouldBe(1);
        fault.RowsAffected.ShouldBe([1]);
        IdentityEmailDeliveryRecord durable = (await new IdentityEmailDeliveryStore(ownerOptions, clock)
            .FindAsync(deliveryId, CancellationToken.None))!;
        durable.State.ShouldBe("delivered");
        durable.LastCompletedLeaseId.ShouldBe(lease.LeaseId);
        durable.LastOutcome.ShouldBe(nameof(EmailAttemptDisposition.Acknowledged));

        (await faultedStore.CompleteAsync(lease, acknowledged, CancellationToken.None)).ShouldBeTrue();
        fault.UpdateAttempts.ShouldBe(2);
        fault.RowsAffected.ShouldBe([1, 0]);

        (await faultedStore.CompleteAsync(lease,
            new EmailAttemptOutcome(EmailAttemptDisposition.PermanentFailure, EmailFailureCode.SmtpRejected, 550),
            CancellationToken.None)).ShouldBeFalse();
        (await faultedStore.CompleteAsync(lease with { LeaseId = Guid.NewGuid() }, acknowledged,
            CancellationToken.None)).ShouldBeFalse();

        IdentityEmailDeliveryRecord unchanged = (await new IdentityEmailDeliveryStore(ownerOptions, clock)
            .FindAsync(deliveryId, CancellationToken.None))!;
        unchanged.State.ShouldBe("delivered");
        unchanged.LastCompletedLeaseId.ShouldBe(lease.LeaseId);
        unchanged.LastOutcome.ShouldBe(nameof(EmailAttemptDisposition.Acknowledged));
        unchanged.LastFailureCode.ShouldBe(nameof(EmailFailureCode.None));
    }

    [TestMethod]
    public async Task OrganizationCompletionRecoversOnlyTheExactOutcomeAfterUpdateAcknowledgementIsLost()
    {
        await using PostgreSqlContainer postgres = await StartPostgresAsync();
        string ownerConnection = await PrepareMigratedDatabaseAsync(postgres);
        DbContextOptions<OrganizationEmailDeliveryDbContext> ownerOptions = OrganizationOptions(ownerConnection);
        Guid deliveryId = Guid.NewGuid();
        Guid organizationId = Guid.NewGuid();
        Guid actorId = Guid.NewGuid();
        await SeedOrganizationAsync(ownerOptions, new OrganizationEmailDeliveryRecord
        {
            Id = deliveryId,
            OrganizationId = organizationId,
            ActorId = actorId,
            MessageId = Guid.NewGuid(),
            AdmissionIdentityKey = "fixture:completion:v1",
            TemplateKey = "fixture.notice",
            TemplateVersion = 1,
            ProtectedIntent = "protected-fixture",
            State = "queued",
            Attempts = 0,
            CreatedAtUtc = Now
        });

        FakeTimeProvider clock = new(Now);
        string runtimeConnection = RoleConnection(ownerConnection,
            PostgresRuntimeRoleFixture.WorkCoordinatorRole, PostgresRuntimeRoleFixture.WorkCoordinatorPassword);
        OrganizationEmailDeliveryStore claimStore = new(OrganizationOptions(runtimeConnection), clock);
        EmailDeliveryLease lease = (await claimStore.ClaimAsync(1, CancellationToken.None)).Single();
        lease.Context.DeliveryId.ShouldBe(deliveryId);
        lease.Context.BoundaryOwnerId.ShouldBe(organizationId);

        CompletionUpdateAcknowledgementFault fault = new("infrastructure.email_deliveries");
        OrganizationEmailDeliveryStore faultedStore = new(OrganizationOptions(runtimeConnection, fault), clock);
        EmailAttemptOutcome acknowledged = new(EmailAttemptDisposition.Acknowledged);

        await Should.ThrowAsync<IOException>(() => faultedStore.CompleteAsync(lease, acknowledged, CancellationToken.None));

        fault.UpdateAttempts.ShouldBe(1);
        fault.RowsAffected.ShouldBe([1]);
        OrganizationEmailDeliveryRecord durable = (await new OrganizationEmailDeliveryStore(ownerOptions, clock)
            .FindAsync(organizationId, deliveryId, CancellationToken.None))!;
        durable.State.ShouldBe("delivered");
        durable.LastCompletedLeaseId.ShouldBe(lease.LeaseId);
        durable.LastOutcome.ShouldBe(nameof(EmailAttemptDisposition.Acknowledged));

        (await faultedStore.CompleteAsync(lease, acknowledged, CancellationToken.None)).ShouldBeTrue();
        fault.UpdateAttempts.ShouldBe(2);
        fault.RowsAffected.ShouldBe([1, 0]);

        (await faultedStore.CompleteAsync(lease,
            new EmailAttemptOutcome(EmailAttemptDisposition.PermanentFailure, EmailFailureCode.SmtpRejected, 550),
            CancellationToken.None)).ShouldBeFalse();
        (await faultedStore.CompleteAsync(lease with { LeaseId = Guid.NewGuid() }, acknowledged,
            CancellationToken.None)).ShouldBeFalse();

        OrganizationEmailDeliveryRecord unchanged = (await new OrganizationEmailDeliveryStore(ownerOptions, clock)
            .FindAsync(organizationId, deliveryId, CancellationToken.None))!;
        unchanged.State.ShouldBe("delivered");
        unchanged.LastCompletedLeaseId.ShouldBe(lease.LeaseId);
        unchanged.LastOutcome.ShouldBe(nameof(EmailAttemptDisposition.Acknowledged));
        unchanged.LastFailureCode.ShouldBe(nameof(EmailFailureCode.None));
    }

    private static async Task<PostgreSqlContainer> StartPostgresAsync()
    {
        PostgreSqlContainer postgres = new PostgreSqlBuilder(
            "postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f").Build();
        await postgres.StartAsync();
        return postgres;
    }

    private static async Task<string> PrepareMigratedDatabaseAsync(PostgreSqlContainer postgres)
    {
        string connection = postgres.GetConnectionString();
        await PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(connection);
        (int exit, string output, string error) = await ModuleJobMigrationIntegrationTests.InvokeMigratorAsync(connection);
        exit.ShouldBe(0, $"Migrator failed.{Environment.NewLine}{error}{Environment.NewLine}{output}");
        return connection;
    }

    private static DbContextOptions<IdentityEmailDeliveryDbContext> IdentityOptions(
        string connection, DbCommandInterceptor? interceptor = null)
    {
        DbContextOptionsBuilder<IdentityEmailDeliveryDbContext> builder = new DbContextOptionsBuilder<IdentityEmailDeliveryDbContext>()
            .UseNpgsql(connection);
        if (interceptor is not null)
            builder.AddInterceptors(interceptor);
        return builder.Options;
    }

    private static DbContextOptions<OrganizationEmailDeliveryDbContext> OrganizationOptions(
        string connection, DbCommandInterceptor? interceptor = null)
    {
        DbContextOptionsBuilder<OrganizationEmailDeliveryDbContext> builder = new DbContextOptionsBuilder<OrganizationEmailDeliveryDbContext>()
            .UseNpgsql(connection);
        if (interceptor is not null)
            builder.AddInterceptors(interceptor);
        return builder.Options;
    }

    private static string RoleConnection(string ownerConnection, string username, string password) =>
        new NpgsqlConnectionStringBuilder(ownerConnection) { Username = username, Password = password }.ConnectionString;

    private static async Task SeedIdentityAsync(DbContextOptions<IdentityEmailDeliveryDbContext> options,
        IdentityEmailDeliveryRecord record)
    {
        await using IdentityEmailDeliveryDbContext context = new(options);
        context.EmailDeliveries.Add(record);
        await context.SaveChangesAsync();
    }

    private static async Task SeedOrganizationAsync(DbContextOptions<OrganizationEmailDeliveryDbContext> options,
        OrganizationEmailDeliveryRecord record)
    {
        await using OrganizationEmailDeliveryDbContext context = new(options);
        context.EmailDeliveries.Add(record);
        await context.SaveChangesAsync();
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CompletionUpdateAcknowledgementFault(string qualifiedTable) : DbCommandInterceptor
    {
        private int armed = 1;
        private int updateAttempts;
        private readonly List<int> rowsAffected = [];

        public int UpdateAttempts => Volatile.Read(ref updateAttempts);
        public int[] RowsAffected
        {
            get
            {
                lock (rowsAffected)
                    return [.. rowsAffected];
            }
        }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (IsCompletionUpdate(command))
            {
                Interlocked.Increment(ref updateAttempts);
                lock (rowsAffected)
                    rowsAffected.Add(result);
                if (Interlocked.CompareExchange(ref armed, 0, 1) == 1)
                    throw new IOException("Injected completion acknowledgement loss after PostgreSQL executed the update.");
            }
            return ValueTask.FromResult(result);
        }

        private bool IsCompletionUpdate(DbCommand command) =>
            command.CommandText.Contains($"UPDATE {qualifiedTable}", StringComparison.Ordinal)
            && command.CommandText.Contains("\"LastCompletedLeaseId\"", StringComparison.Ordinal)
            && command.CommandText.Contains("\"LastOutcome\"", StringComparison.Ordinal);
    }
}
#endif
