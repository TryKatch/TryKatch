using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Application.Mailing;
using Trykatch.Identity;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class IdentityEmailDeliveryStoreTests
{
    private const string ProtectedIntent = "protected-email-intent";

    [TestMethod]
    public async Task StorePersistsImmutableIntentClaimsExclusivelyAndCompletesWithRetryPolicy()
    {
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder(
            "postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f").Build();
        await postgres.StartAsync();
        DbContextOptions<IdentityEmailDeliveryDbContext> options = new DbContextOptionsBuilder<IdentityEmailDeliveryDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options;
        await using (IdentityEmailDeliveryDbContext setup = new(options))
            await setup.Database.EnsureCreatedAsync();

        FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
        IdentityEmailDeliveryStore store = new(options, clock);
        Guid id = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        IdentityEmailDeliveryRecord record = NewRecord(id, userId);
        (await store.TryInsertAsync(record, CancellationToken.None)).ShouldBeTrue();
        (await store.TryInsertAsync(NewRecord(id, Guid.NewGuid(), "replacement"), CancellationToken.None)).ShouldBeFalse();

        IdentityEmailDeliveryRecord? persisted = await new IdentityEmailDeliveryStore(options, clock)
            .FindAsync(id, CancellationToken.None);
        persisted.ShouldNotBeNull();
        persisted.UserId.ShouldBe(userId);
        persisted.ProtectedIntent.ShouldBe(ProtectedIntent);
        persisted.TemplateKey.ShouldBe("password-reset");

        Task<IReadOnlyList<EmailDeliveryLease>> firstClaim = store.ClaimAsync(10, CancellationToken.None);
        Task<IReadOnlyList<EmailDeliveryLease>> competingClaim = store.ClaimAsync(10, CancellationToken.None);
        IReadOnlyList<EmailDeliveryLease>[] claims = await Task.WhenAll(firstClaim, competingClaim);
        claims.Sum(batch => batch.Count).ShouldBe(1);
        EmailDeliveryLease lease = claims.SelectMany(batch => batch).Single();
        lease.Context.ShouldBe(new EmailDeliveryProtectionContext(EmailDeliveryBoundary.Identity, userId, id, "password-reset", 1));
        lease.Attempts.ShouldBe(1);
        lease.ProtectedIntent.ShouldBe(ProtectedIntent);

        (await store.CompleteAsync(lease with { LeaseId = Guid.NewGuid() },
            new EmailAttemptOutcome(EmailAttemptDisposition.Acknowledged), CancellationToken.None)).ShouldBeFalse();
        EmailAttemptOutcome retry = new(EmailAttemptDisposition.RetryableFailure, EmailFailureCode.ConnectionFailed);
        (await store.CompleteAsync(lease, retry, CancellationToken.None)).ShouldBeTrue();
        (await store.CompleteAsync(lease, retry, CancellationToken.None)).ShouldBeTrue();
        (await store.CompleteAsync(lease, new EmailAttemptOutcome(EmailAttemptDisposition.Acknowledged),
            CancellationToken.None)).ShouldBeFalse();

        IdentityEmailDeliveryRecord afterRetry = (await store.FindAsync(id, CancellationToken.None))!;
        afterRetry.State.ShouldBe("queued");
        afterRetry.Attempts.ShouldBe(1);
        afterRetry.LastCompletedLeaseId.ShouldBe(lease.LeaseId);
        afterRetry.LastOutcome.ShouldBe(nameof(EmailAttemptDisposition.RetryableFailure));
        afterRetry.NextAttemptAtUtc.ShouldBe(clock.GetUtcNow().AddSeconds(5));

        clock.Advance(TimeSpan.FromSeconds(5));
        EmailDeliveryLease secondLease = (await store.ClaimAsync(1, CancellationToken.None)).Single();
        secondLease.Attempts.ShouldBe(2);
        (await store.CompleteAsync(lease, retry, CancellationToken.None)).ShouldBeTrue();
        IdentityEmailDeliveryRecord stillLeased = (await store.FindAsync(id, CancellationToken.None))!;
        stillLeased.State.ShouldBe("leased");
        stillLeased.LeaseId.ShouldBe(secondLease.LeaseId);
        (await store.CompleteAsync(secondLease,
            new EmailAttemptOutcome(EmailAttemptDisposition.PermanentFailure, EmailFailureCode.SmtpRejected, 550),
            CancellationToken.None)).ShouldBeTrue();
        IdentityEmailDeliveryRecord completed = (await store.FindAsync(id, CancellationToken.None))!;
        completed.State.ShouldBe("terminal");
        completed.Attempts.ShouldBe(2);
    }

    [TestMethod]
    public async Task ExpiredLeaseAttemptsAtLimitBecomeTerminalInsteadOfBeingStranded()
    {
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder(
            "postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f").Build();
        await postgres.StartAsync();
        DbContextOptions<IdentityEmailDeliveryDbContext> options = new DbContextOptionsBuilder<IdentityEmailDeliveryDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options;
        await using (IdentityEmailDeliveryDbContext setup = new(options))
            await setup.Database.EnsureCreatedAsync();
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
        IdentityEmailDeliveryStore store = new(options, clock);
        IdentityEmailDeliveryRecord record = NewRecord(Guid.NewGuid(), Guid.NewGuid());
        record.State = "leased";
        record.Attempts = EmailDeliveryPolicy.MaximumAttempts;
        record.LeaseId = Guid.NewGuid();
        record.LeaseUntilUtc = clock.GetUtcNow().AddSeconds(-1);
        await using (IdentityEmailDeliveryDbContext seed = new(options))
        {
            seed.EmailDeliveries.Add(record);
            await seed.SaveChangesAsync();
        }

        (await store.ClaimAsync(5, CancellationToken.None)).ShouldBeEmpty();
        IdentityEmailDeliveryRecord exhausted = (await store.FindAsync(record.Id, CancellationToken.None))!;
        exhausted.State.ShouldBe("terminal");
        exhausted.LastFailureCode.ShouldBe(nameof(EmailFailureCode.AttemptsExhausted));
        exhausted.CompletedAtUtc.ShouldBe(clock.GetUtcNow());
    }

    [TestMethod]
    public async Task ExhaustedLeaseCleanupHonorsClaimLimit()
    {
        await using PostgreSqlContainer postgres = await StartPostgresAsync();
        DbContextOptions<IdentityEmailDeliveryDbContext> options = await CreateSchemaAsync(postgres);
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
        IdentityEmailDeliveryStore store = new(options, clock);
        IdentityEmailDeliveryRecord[] rows = CreateExhaustedRows(clock.GetUtcNow());
        await SeedAsync(options, rows);

        (await store.ClaimAsync(1, CancellationToken.None)).ShouldBeEmpty();

        IdentityEmailDeliveryRecord[] current = await Task.WhenAll(rows.Select(async row =>
            (await store.FindAsync(row.Id, CancellationToken.None))!));
        current.Count(row => row.State == "terminal").ShouldBe(1);
        current.Count(row => row.State == "leased").ShouldBe(2);
    }

    [TestMethod]
    public async Task ExhaustedLeaseCleanupSkipsLockedRowAndContinuesWithinLimit()
    {
        await using PostgreSqlContainer postgres = await StartPostgresAsync();
        DbContextOptions<IdentityEmailDeliveryDbContext> options = await CreateSchemaAsync(postgres);
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
        IdentityEmailDeliveryStore store = new(options, clock);
        IdentityEmailDeliveryRecord[] rows = CreateExhaustedRows(clock.GetUtcNow());
        await SeedAsync(options, rows);

        await using NpgsqlConnection connection = new(postgres.GetConnectionString());
        await connection.OpenAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using (NpgsqlCommand lockRow = new("SELECT \"Id\" FROM identity.email_deliveries WHERE \"Id\" = @id FOR UPDATE", connection, transaction))
        {
            lockRow.Parameters.AddWithValue("id", rows[0].Id);
            (await lockRow.ExecuteScalarAsync()).ShouldBe(rows[0].Id);
        }

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        try
        {
            (await store.ClaimAsync(1, timeout.Token).WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeEmpty();
        }
        finally
        {
            await transaction.RollbackAsync();
        }

        IdentityEmailDeliveryRecord first = (await store.FindAsync(rows[0].Id, CancellationToken.None))!;
        IdentityEmailDeliveryRecord second = (await store.FindAsync(rows[1].Id, CancellationToken.None))!;
        IdentityEmailDeliveryRecord third = (await store.FindAsync(rows[2].Id, CancellationToken.None))!;
        first.State.ShouldBe("leased");
        second.State.ShouldBe("terminal");
        third.State.ShouldBe("leased");
    }

    [TestMethod]
    public async Task LostCommitAcknowledgementRecoversOnlyTheExactCommittedLease()
    {
        await using PostgreSqlContainer postgres = await StartPostgresAsync();
        DbContextOptions<IdentityEmailDeliveryDbContext> baseOptions = await CreateSchemaAsync(postgres);
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
        IdentityEmailDeliveryRecord record = NewRecord(Guid.NewGuid(), Guid.NewGuid());
        await SeedAsync(baseOptions, record);

        CommitAcknowledgementFault fault = new();
        DbContextOptions<IdentityEmailDeliveryDbContext> options = new DbContextOptionsBuilder<IdentityEmailDeliveryDbContext>()
            .UseNpgsql(postgres.GetConnectionString())
            .AddInterceptors(fault)
            .Options;
        IdentityEmailDeliveryStore store = new(options, clock);

        IReadOnlyList<EmailDeliveryLease> recovered = await store.ClaimAsync(1, CancellationToken.None);

        fault.FaultCount.ShouldBe(1);
        recovered.Count.ShouldBe(1);
        recovered[0].Context.DeliveryId.ShouldBe(record.Id);
        IdentityEmailDeliveryRecord persisted = (await new IdentityEmailDeliveryStore(baseOptions, clock)
            .FindAsync(record.Id, CancellationToken.None))!;
        persisted.State.ShouldBe("leased");
        persisted.LeaseId.ShouldBe(recovered[0].LeaseId);
    }

    private static async Task<PostgreSqlContainer> StartPostgresAsync()
    {
        PostgreSqlContainer postgres = new PostgreSqlBuilder(
            "postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f").Build();
        await postgres.StartAsync();
        return postgres;
    }

    private static async Task<DbContextOptions<IdentityEmailDeliveryDbContext>> CreateSchemaAsync(PostgreSqlContainer postgres)
    {
        DbContextOptions<IdentityEmailDeliveryDbContext> options = new DbContextOptionsBuilder<IdentityEmailDeliveryDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options;
        await using IdentityEmailDeliveryDbContext setup = new(options);
        await setup.Database.EnsureCreatedAsync();
        return options;
    }

    private static IdentityEmailDeliveryRecord[] CreateExhaustedRows(DateTimeOffset now) =>
    [
        CreateExhaustedRecord(Guid.Parse("00000000-0000-0000-0000-000000000101"), now),
        CreateExhaustedRecord(Guid.Parse("00000000-0000-0000-0000-000000000102"), now),
        CreateExhaustedRecord(Guid.Parse("00000000-0000-0000-0000-000000000103"), now)
    ];

    private static IdentityEmailDeliveryRecord CreateExhaustedRecord(Guid id, DateTimeOffset now)
    {
        IdentityEmailDeliveryRecord record = NewRecord(id, Guid.NewGuid());
        record.State = "leased";
        record.Attempts = EmailDeliveryPolicy.MaximumAttempts;
        record.LeaseId = Guid.NewGuid();
        record.LeaseUntilUtc = now.AddSeconds(-1);
        record.CreatedAtUtc = now.AddMinutes(-1);
        return record;
    }

    private static async Task SeedAsync(DbContextOptions<IdentityEmailDeliveryDbContext> options, params IdentityEmailDeliveryRecord[] rows)
    {
        await using IdentityEmailDeliveryDbContext seed = new(options);
        seed.EmailDeliveries.AddRange(rows);
        await seed.SaveChangesAsync();
    }

    private static IdentityEmailDeliveryRecord NewRecord(Guid id, Guid userId, string protectedIntent = ProtectedIntent) => new()
    {
        Id = id,
        UserId = userId,
        TemplateKey = "password-reset",
        TemplateVersion = 1,
        ProtectedIntent = protectedIntent,
        State = "queued",
        Attempts = 0,
        NextAttemptAtUtc = null
    };

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now = _now.Add(amount);
    }

    private sealed class CommitAcknowledgementFault : DbTransactionInterceptor
    {
        private int _faultCount;
        public int FaultCount => Volatile.Read(ref _faultCount);

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context is IdentityEmailDeliveryDbContext && Interlocked.Increment(ref _faultCount) == 1)
                throw new IOException("Simulated lost commit acknowledgement after PostgreSQL committed.");
            return Task.CompletedTask;
        }
    }
}
