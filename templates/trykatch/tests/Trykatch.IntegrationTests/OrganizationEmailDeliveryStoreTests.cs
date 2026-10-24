using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Application.Mailing;
using Trykatch.Infrastructure.Mailing;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
// Owner-backed disposable PostgreSQL fixtures verify store mechanics, not production runtime-role grants.
public sealed class OrganizationEmailDeliveryStoreTests
{
    private const string ProtectedIntent = "protected-organization-email-intent";

    [TestMethod]
    public async Task StoreLooksUpByOrganizationAndAdmissionAndCompletesWithRetryPolicy()
    {
        await using PostgreSqlContainer postgres = await StartPostgresAsync();
        DbContextOptions<OrganizationEmailDeliveryDbContext> options = await CreateSchemaAsync(postgres);
        FakeTimeProvider clock = NewClock();
        OrganizationEmailDeliveryStore store = new(options, clock);
        Guid organizationId = Guid.NewGuid();
        Guid actorId = Guid.NewGuid();
        Guid id = Guid.NewGuid();
        Guid messageId = Guid.NewGuid();
        const string admissionIdentity = "member:password-reset:v1";
        OrganizationEmailDeliveryRecord record = NewRecord(id, organizationId, actorId, messageId, admissionIdentity);
        await SeedAsync(options, record);

        OrganizationEmailDeliveryRecord? found = await store.FindAsync(organizationId, id, CancellationToken.None);
        found.ShouldNotBeNull();
        found.AdmissionIdentityKey.ShouldBe(admissionIdentity);
        found.ProtectedIntent.ShouldBe(ProtectedIntent);
        (await store.FindAsync(Guid.NewGuid(), id, CancellationToken.None)).ShouldBeNull();
        (await store.FindByAdmissionAsync(organizationId, admissionIdentity, record.TemplateKey,
            record.TemplateVersion, messageId, CancellationToken.None))!.Id.ShouldBe(id);
        (await store.FindByAdmissionAsync(Guid.NewGuid(), admissionIdentity, record.TemplateKey,
            record.TemplateVersion, messageId, CancellationToken.None)).ShouldBeNull();
        (await store.FindByAdmissionAsync(organizationId, "other-admission", record.TemplateKey,
            record.TemplateVersion, messageId, CancellationToken.None)).ShouldBeNull();
        (await store.FindByAdmissionAsync(organizationId, admissionIdentity, "other-template",
            record.TemplateVersion, messageId, CancellationToken.None)).ShouldBeNull();
        (await store.FindByAdmissionAsync(organizationId, admissionIdentity, record.TemplateKey,
            record.TemplateVersion + 1, messageId, CancellationToken.None)).ShouldBeNull();
        (await store.FindByAdmissionAsync(organizationId, admissionIdentity, record.TemplateKey,
            record.TemplateVersion, Guid.NewGuid(), CancellationToken.None)).ShouldBeNull();

        Task<IReadOnlyList<EmailDeliveryLease>> firstClaim = store.ClaimAsync(10, CancellationToken.None);
        Task<IReadOnlyList<EmailDeliveryLease>> competingClaim = store.ClaimAsync(10, CancellationToken.None);
        IReadOnlyList<EmailDeliveryLease>[] claims = await Task.WhenAll(firstClaim, competingClaim);
        claims.Sum(batch => batch.Count).ShouldBe(1);
        EmailDeliveryLease lease = claims.SelectMany(batch => batch).Single();
        lease.Context.ShouldBe(new EmailDeliveryProtectionContext(
            EmailDeliveryBoundary.Organization, organizationId, id, record.TemplateKey, record.TemplateVersion));
        lease.Attempts.ShouldBe(1);
        lease.ProtectedIntent.ShouldBe(ProtectedIntent);

        EmailAttemptOutcome retry = new(EmailAttemptDisposition.RetryableFailure, EmailFailureCode.ConnectionFailed);
        (await store.CompleteAsync(lease with
        {
            Context = lease.Context with { BoundaryOwnerId = Guid.NewGuid() }
        }, retry, CancellationToken.None)).ShouldBeFalse();
        (await store.CompleteAsync(lease, retry, CancellationToken.None)).ShouldBeTrue();
        (await store.CompleteAsync(lease, retry, CancellationToken.None)).ShouldBeTrue();
        (await store.CompleteAsync(lease, new EmailAttemptOutcome(EmailAttemptDisposition.Acknowledged),
            CancellationToken.None)).ShouldBeFalse();

        OrganizationEmailDeliveryRecord afterRetry = (await store.FindAsync(organizationId, id, CancellationToken.None))!;
        afterRetry.State.ShouldBe("queued");
        afterRetry.Attempts.ShouldBe(1);
        afterRetry.LastCompletedLeaseId.ShouldBe(lease.LeaseId);
        afterRetry.LastOutcome.ShouldBe(nameof(EmailAttemptDisposition.RetryableFailure));
        afterRetry.NextAttemptAtUtc.ShouldBe(clock.GetUtcNow().AddSeconds(5));

        clock.Advance(TimeSpan.FromSeconds(5));
        EmailDeliveryLease secondLease = (await store.ClaimAsync(1, CancellationToken.None)).Single();
        secondLease.Attempts.ShouldBe(2);
        (await store.CompleteAsync(lease, retry, CancellationToken.None)).ShouldBeTrue();
        OrganizationEmailDeliveryRecord stillLeased = (await store.FindAsync(organizationId, id, CancellationToken.None))!;
        stillLeased.State.ShouldBe("leased");
        stillLeased.LeaseId.ShouldBe(secondLease.LeaseId);
        (await store.CompleteAsync(secondLease,
            new EmailAttemptOutcome(EmailAttemptDisposition.PermanentFailure, EmailFailureCode.SmtpRejected, 550),
            CancellationToken.None)).ShouldBeTrue();
        OrganizationEmailDeliveryRecord completed = (await store.FindAsync(organizationId, id, CancellationToken.None))!;
        completed.State.ShouldBe("terminal");
        completed.Attempts.ShouldBe(2);
    }

    [TestMethod]
    public async Task ExpiredLeaseCanBeReclaimedWithANewLeaseIdentity()
    {
        await using PostgreSqlContainer postgres = await StartPostgresAsync();
        DbContextOptions<OrganizationEmailDeliveryDbContext> options = await CreateSchemaAsync(postgres);
        FakeTimeProvider clock = NewClock();
        OrganizationEmailDeliveryStore store = new(options, clock);
        OrganizationEmailDeliveryRecord record = CreateWithLease(
            NewRecord(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "admission"),
            attempts: 1, leaseId: Guid.NewGuid(), leaseUntil: clock.GetUtcNow().AddSeconds(-1));
        Guid oldLeaseId = record.LeaseId!.Value;
        await SeedAsync(options, record);

        EmailDeliveryLease lease = (await store.ClaimAsync(1, CancellationToken.None)).Single();

        lease.Context.DeliveryId.ShouldBe(record.Id);
        lease.LeaseId.ShouldNotBe(oldLeaseId);
        lease.Attempts.ShouldBe(2);
        EmailDeliveryLease staleLease = new(
            new EmailDeliveryProtectionContext(EmailDeliveryBoundary.Organization, record.OrganizationId,
                record.Id, record.TemplateKey, record.TemplateVersion),
            oldLeaseId, 1, record.LeaseUntilUtc!.Value, record.ProtectedIntent);
        (await store.CompleteAsync(staleLease, new EmailAttemptOutcome(EmailAttemptDisposition.Acknowledged),
            CancellationToken.None)).ShouldBeFalse();
    }

    [TestMethod]
    public async Task RetryBudgetTerminalizesTheFifthAttempt()
    {
        await using PostgreSqlContainer postgres = await StartPostgresAsync();
        DbContextOptions<OrganizationEmailDeliveryDbContext> options = await CreateSchemaAsync(postgres);
        FakeTimeProvider clock = NewClock();
        OrganizationEmailDeliveryStore store = new(options, clock);
        Guid organizationId = Guid.NewGuid();
        OrganizationEmailDeliveryRecord record = NewRecord(Guid.NewGuid(), organizationId, Guid.NewGuid(), Guid.NewGuid(), "admission");
        await SeedAsync(options, record);

        for (int attempt = 1; attempt <= EmailDeliveryPolicy.MaximumAttempts; attempt++)
        {
            if (attempt > 1)
                clock.Advance(TimeSpan.FromSeconds(5 * (1 << (attempt - 2))));
            EmailDeliveryLease lease = (await store.ClaimAsync(1, CancellationToken.None)).Single();
            lease.Attempts.ShouldBe(attempt);
            (await store.CompleteAsync(lease,
                new EmailAttemptOutcome(EmailAttemptDisposition.RetryableFailure, EmailFailureCode.ConnectionFailed),
                CancellationToken.None)).ShouldBeTrue();
        }

        OrganizationEmailDeliveryRecord exhausted = (await store.FindAsync(organizationId, record.Id, CancellationToken.None))!;
        exhausted.State.ShouldBe("terminal");
        exhausted.Attempts.ShouldBe(EmailDeliveryPolicy.MaximumAttempts);
    }

    [TestMethod]
    public async Task ExhaustedLeaseCleanupHonorsClaimLimit()
    {
        await using PostgreSqlContainer postgres = await StartPostgresAsync();
        DbContextOptions<OrganizationEmailDeliveryDbContext> options = await CreateSchemaAsync(postgres);
        FakeTimeProvider clock = NewClock();
        OrganizationEmailDeliveryStore store = new(options, clock);
        OrganizationEmailDeliveryRecord[] rows = CreateExhaustedRows(clock.GetUtcNow());
        await SeedAsync(options, rows);

        (await store.ClaimAsync(1, CancellationToken.None)).ShouldBeEmpty();

        OrganizationEmailDeliveryRecord[] current = await Task.WhenAll(rows.Select(async row =>
            (await store.FindAsync(row.OrganizationId, row.Id, CancellationToken.None))!));
        current.Count(row => row.State == "terminal").ShouldBe(1);
        current.Count(row => row.State == "leased").ShouldBe(2);
    }

    [TestMethod]
    public async Task ExhaustedLeaseCleanupSkipsLockedRowAndContinuesWithinLimit()
    {
        await using PostgreSqlContainer postgres = await StartPostgresAsync();
        DbContextOptions<OrganizationEmailDeliveryDbContext> options = await CreateSchemaAsync(postgres);
        FakeTimeProvider clock = NewClock();
        OrganizationEmailDeliveryStore store = new(options, clock);
        OrganizationEmailDeliveryRecord[] rows = CreateExhaustedRows(clock.GetUtcNow());
        await SeedAsync(options, rows);

        await using NpgsqlConnection connection = new(postgres.GetConnectionString());
        await connection.OpenAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using (NpgsqlCommand lockRow = new("SELECT \"Id\" FROM infrastructure.email_deliveries WHERE \"Id\" = @id FOR UPDATE", connection, transaction))
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

        OrganizationEmailDeliveryRecord first = (await store.FindAsync(rows[0].OrganizationId, rows[0].Id, CancellationToken.None))!;
        OrganizationEmailDeliveryRecord second = (await store.FindAsync(rows[1].OrganizationId, rows[1].Id, CancellationToken.None))!;
        OrganizationEmailDeliveryRecord third = (await store.FindAsync(rows[2].OrganizationId, rows[2].Id, CancellationToken.None))!;
        first.State.ShouldBe("leased");
        second.State.ShouldBe("terminal");
        third.State.ShouldBe("leased");
    }

    [TestMethod]
    public async Task LostCommitAcknowledgementRecoversOnlyTheExactCommittedLease()
    {
        await using PostgreSqlContainer postgres = await StartPostgresAsync();
        DbContextOptions<OrganizationEmailDeliveryDbContext> baseOptions = await CreateSchemaAsync(postgres);
        FakeTimeProvider clock = NewClock();
        OrganizationEmailDeliveryRecord record = NewRecord(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "admission");
        await SeedAsync(baseOptions, record);

        CommitAcknowledgementFault fault = new();
        DbContextOptions<OrganizationEmailDeliveryDbContext> options = new DbContextOptionsBuilder<OrganizationEmailDeliveryDbContext>()
            .UseNpgsql(postgres.GetConnectionString())
            .AddInterceptors(fault)
            .Options;
        OrganizationEmailDeliveryStore store = new(options, clock);

        IReadOnlyList<EmailDeliveryLease> recovered = await store.ClaimAsync(1, CancellationToken.None);

        fault.FaultCount.ShouldBe(1);
        recovered.Count.ShouldBe(1);
        recovered[0].Context.DeliveryId.ShouldBe(record.Id);
        OrganizationEmailDeliveryRecord persisted = (await new OrganizationEmailDeliveryStore(baseOptions, clock)
            .FindAsync(record.OrganizationId, record.Id, CancellationToken.None))!;
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

    private static async Task<DbContextOptions<OrganizationEmailDeliveryDbContext>> CreateSchemaAsync(PostgreSqlContainer postgres)
    {
        DbContextOptions<OrganizationEmailDeliveryDbContext> options = new DbContextOptionsBuilder<OrganizationEmailDeliveryDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options;
        await using OrganizationEmailDeliveryDbContext setup = new(options);
        await setup.Database.EnsureCreatedAsync();
        return options;
    }

    private static OrganizationEmailDeliveryRecord[] CreateExhaustedRows(DateTimeOffset now) =>
    [
        CreateExhaustedRecord(Guid.Parse("00000000-0000-0000-0000-000000000201"), now),
        CreateExhaustedRecord(Guid.Parse("00000000-0000-0000-0000-000000000202"), now),
        CreateExhaustedRecord(Guid.Parse("00000000-0000-0000-0000-000000000203"), now)
    ];

    private static OrganizationEmailDeliveryRecord CreateExhaustedRecord(Guid id, DateTimeOffset now) =>
        CreateWithLease(NewRecord(id, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "admission"),
            attempts: EmailDeliveryPolicy.MaximumAttempts, leaseId: Guid.NewGuid(), leaseUntil: now.AddSeconds(-1));

    private static OrganizationEmailDeliveryRecord NewRecord(
        Guid id, Guid organizationId, Guid actorId, Guid messageId, string admissionIdentity) => new()
    {
        Id = id,
        OrganizationId = organizationId,
        ActorId = actorId,
        MessageId = messageId,
        AdmissionIdentityKey = admissionIdentity,
        TemplateKey = "password-reset",
        TemplateVersion = 1,
        ProtectedIntent = ProtectedIntent,
        State = "queued",
        Attempts = 0,
        CreatedAtUtc = new DateTimeOffset(2026, 10, 24, 11, 0, 0, TimeSpan.Zero)
    };

    private static async Task SeedAsync(DbContextOptions<OrganizationEmailDeliveryDbContext> options,
        params OrganizationEmailDeliveryRecord[] records)
    {
        await using OrganizationEmailDeliveryDbContext seed = new(options);
        seed.EmailDeliveries.AddRange(records);
        await seed.SaveChangesAsync();
    }

    private static FakeTimeProvider NewClock() => new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));

    private static OrganizationEmailDeliveryRecord CreateWithLease(
        OrganizationEmailDeliveryRecord record, int attempts, Guid leaseId, DateTimeOffset leaseUntil)
    {
        record.Attempts = attempts;
        record.State = "leased";
        record.LeaseId = leaseId;
        record.LeaseUntilUtc = leaseUntil;
        return record;
    }

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
            if (eventData.Context is OrganizationEmailDeliveryDbContext && Interlocked.Increment(ref _faultCount) == 1)
                throw new IOException("Simulated lost commit acknowledgement after PostgreSQL committed.");
            return Task.CompletedTask;
        }
    }
}
