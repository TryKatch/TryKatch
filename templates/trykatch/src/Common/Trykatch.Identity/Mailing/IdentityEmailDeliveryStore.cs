using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using System.Runtime.ExceptionServices;
using Trykatch.Application.Mailing;

namespace Trykatch.Identity;

internal sealed class IdentityEmailDeliveryStore(
    DbContextOptions<IdentityEmailDeliveryDbContext> options,
    TimeProvider timeProvider)
{
    private const string Queued = "queued";
    private const string Leased = "leased";

    public async Task<bool> TryInsertAsync(IdentityEmailDeliveryRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ValidateForInsert(record);
        await using IdentityEmailDeliveryDbContext context = CreateContext();
        DateTimeOffset created = record.CreatedAtUtc == default ? timeProvider.GetUtcNow().ToUniversalTime() : record.CreatedAtUtc.ToUniversalTime();
        int inserted = await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO identity.email_deliveries
                ("Id", "UserId", "TemplateKey", "TemplateVersion", "ProtectedIntent", "State", "Attempts",
                 "LeaseId", "LeaseUntilUtc", "NextAttemptAtUtc", "LastCompletedLeaseId", "LastOutcome",
                 "LastFailureCode", "LastSmtpStatusCode", "CreatedAtUtc", "CompletedAtUtc")
            VALUES ({record.Id}, {record.UserId}, {record.TemplateKey}, {record.TemplateVersion}, {record.ProtectedIntent},
                    {record.State}, {record.Attempts}, {record.LeaseId}, {record.LeaseUntilUtc}, {record.NextAttemptAtUtc},
                    {record.LastCompletedLeaseId}, {record.LastOutcome}, {record.LastFailureCode}, {record.LastSmtpStatusCode},
                    {created}, {record.CompletedAtUtc})
            ON CONFLICT ("Id") DO NOTHING
            """, cancellationToken);
        return inserted == 1;
    }

    public async Task<IdentityEmailDeliveryRecord?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        await using IdentityEmailDeliveryDbContext context = CreateContext();
        return await context.EmailDeliveries.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<EmailDeliveryLease>> ClaimAsync(int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(limit), "Claim limit must be between 1 and 100.");

        ClaimTransactionResult result = await ClaimInTransactionAsync(limit, cancellationToken);
        if (result.CommitFailure is null)
            return result.Leases;

        try
        {
            using CancellationTokenSource recoveryTimeout = new(TimeSpan.FromSeconds(5));
            List<EmailDeliveryLease> recovered = await RecoverAssignedLeasesAsync(result.Assigned, recoveryTimeout.Token);
            if (recovered.Count == result.Assigned.Count)
                return recovered;
        }
        catch
        {
            // Recovery is best-effort and bounded; preserve the original commit error below.
        }

        ExceptionDispatchInfo.Capture(result.CommitFailure!).Throw();
        throw new InvalidOperationException("Unreachable after rethrowing the email delivery claim commit failure.");
    }

    private async Task<ClaimTransactionResult> ClaimInTransactionAsync(int limit, CancellationToken cancellationToken)
    {
        List<(Guid LeaseId, Guid DeliveryId)> assigned = [];
        List<EmailDeliveryLease> leases = [];
        Exception? commitFailure = null;
        await using (IdentityEmailDeliveryDbContext context = CreateContext())
        {
            DateTimeOffset now = timeProvider.GetUtcNow().ToUniversalTime();
            await using (IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken))
            {
                NpgsqlConnection connection = (NpgsqlConnection)context.Database.GetDbConnection();
                NpgsqlTransaction postgresTransaction = (NpgsqlTransaction)transaction.GetDbTransaction();

                await using (NpgsqlCommand exhausted = new("""
                    WITH expired_exhausted AS (
                        SELECT "Id"
                        FROM identity.email_deliveries
                        WHERE "State" = 'leased' AND "LeaseUntilUtc" <= @now AND "Attempts" >= @maximumAttempts
                        ORDER BY "LeaseUntilUtc", "CreatedAtUtc", "Id"
                        LIMIT @limit
                        FOR UPDATE SKIP LOCKED
                    )
                    UPDATE identity.email_deliveries AS delivery
                    SET "State" = 'terminal', "LastCompletedLeaseId" = delivery."LeaseId", "LastOutcome" = 'Uncertain',
                        "LastFailureCode" = 'AttemptsExhausted', "LastSmtpStatusCode" = NULL,
                        "LeaseId" = NULL, "LeaseUntilUtc" = NULL, "NextAttemptAtUtc" = NULL,
                        "CompletedAtUtc" = @now
                    FROM expired_exhausted
                    WHERE delivery."Id" = expired_exhausted."Id"
                    """, connection, postgresTransaction))
                {
                    exhausted.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
                    exhausted.Parameters.AddWithValue("maximumAttempts", EmailDeliveryPolicy.MaximumAttempts);
                    exhausted.Parameters.AddWithValue("limit", limit);
                    await exhausted.ExecuteNonQueryAsync(cancellationToken);
                }

                List<Guid> candidateIds = [];
                await using (NpgsqlCommand select = new("""
                    SELECT "Id"
                    FROM identity.email_deliveries
                    WHERE "Attempts" < @maximumAttempts AND (
                        ("State" = 'queued' AND ("NextAttemptAtUtc" IS NULL OR "NextAttemptAtUtc" <= @now))
                        OR ("State" = 'leased' AND "LeaseUntilUtc" <= @now))
                    ORDER BY COALESCE("NextAttemptAtUtc", "CreatedAtUtc"), "CreatedAtUtc", "Id"
                    FOR UPDATE SKIP LOCKED
                    LIMIT @limit
                    """, connection, postgresTransaction))
                {
                    select.Parameters.AddWithValue("maximumAttempts", EmailDeliveryPolicy.MaximumAttempts);
                    select.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
                    select.Parameters.AddWithValue("limit", limit);
                    await using NpgsqlDataReader reader = await select.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                        candidateIds.Add(reader.GetGuid(0));
                }

                foreach (Guid id in candidateIds)
                {
                    Guid leaseId = Guid.NewGuid();
                    await using NpgsqlCommand update = new("""
                        UPDATE identity.email_deliveries
                        SET "State" = 'leased', "LeaseId" = @leaseId,
                            "LeaseUntilUtc" = @leaseUntil, "NextAttemptAtUtc" = NULL,
                            "Attempts" = "Attempts" + 1, "CompletedAtUtc" = NULL
                        WHERE "Id" = @id AND "Attempts" < @maximumAttempts AND (
                            ("State" = 'queued' AND ("NextAttemptAtUtc" IS NULL OR "NextAttemptAtUtc" <= @now))
                            OR ("State" = 'leased' AND "LeaseUntilUtc" <= @now))
                        """, connection, postgresTransaction);
                    update.Parameters.AddWithValue("leaseId", leaseId);
                    update.Parameters.AddWithValue("leaseUntil", NpgsqlDbType.TimestampTz, now.Add(EmailDeliveryPolicy.LeaseDuration));
                    update.Parameters.AddWithValue("id", id);
                    update.Parameters.AddWithValue("maximumAttempts", EmailDeliveryPolicy.MaximumAttempts);
                    update.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
                    if (await update.ExecuteNonQueryAsync(cancellationToken) == 1)
                        assigned.Add((leaseId, id));
                }

                leases = await ReadAssignedLeasesAsync(context, assigned, cancellationToken);
                try
                {
                    await transaction.CommitAsync(cancellationToken);
                }
                catch (Exception failure)
                {
                    commitFailure = failure;
                }
            }
        }

        return new ClaimTransactionResult(leases, assigned, commitFailure);
    }

    public async Task<bool> CompleteAsync(EmailDeliveryLease lease, EmailAttemptOutcome outcome, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(outcome);
        if (lease.Context.Boundary != EmailDeliveryBoundary.Identity)
            return false;
        int attempts = lease.Attempts;
        EmailDeliveryTransition transition = EmailDeliveryPolicy.Complete(outcome, attempts, timeProvider.GetUtcNow());
        string state = ToState(transition.Status);
        DateTimeOffset? completedAt = transition.Status is EmailDeliveryStatus.Queued ? null : timeProvider.GetUtcNow().ToUniversalTime();
        await using IdentityEmailDeliveryDbContext context = CreateContext();
        int updated = await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE identity.email_deliveries
            SET "State" = {state}, "LeaseId" = NULL, "LeaseUntilUtc" = NULL,
                "NextAttemptAtUtc" = {transition.NextAttemptAtUtc}, "LastCompletedLeaseId" = {lease.LeaseId},
                "LastOutcome" = {outcome.Disposition.ToString()}, "LastFailureCode" = {outcome.FailureCode.ToString()},
                "LastSmtpStatusCode" = {outcome.SmtpStatusCode}, "CompletedAtUtc" = {completedAt}
            WHERE "Id" = {lease.Context.DeliveryId} AND "UserId" = {lease.Context.BoundaryOwnerId}
                AND "TemplateKey" = {lease.Context.TemplateKey} AND "TemplateVersion" = {lease.Context.TemplateVersion}
                AND "State" = 'leased' AND "LeaseId" = {lease.LeaseId} AND "Attempts" = {attempts}
            """, cancellationToken);
        if (updated == 1)
            return true;

        return await context.EmailDeliveries.AsNoTracking().AnyAsync(row =>
            row.Id == lease.Context.DeliveryId && row.UserId == lease.Context.BoundaryOwnerId
            && row.TemplateKey == lease.Context.TemplateKey && row.TemplateVersion == lease.Context.TemplateVersion
            && row.LastCompletedLeaseId == lease.LeaseId
            && row.LastOutcome == outcome.Disposition.ToString()
            && row.LastFailureCode == outcome.FailureCode.ToString()
            && row.LastSmtpStatusCode == outcome.SmtpStatusCode, cancellationToken);
    }

    private IdentityEmailDeliveryDbContext CreateContext()
    {
        IdentityEmailDeliveryDbContext context = new(options);
        if (context.Database.CreateExecutionStrategy().RetriesOnFailure)
        {
            context.Dispose();
            throw new InvalidOperationException("Identity email delivery storage cannot use a retrying execution strategy.");
        }
        return context;
    }

    private static async Task<List<EmailDeliveryLease>> ReadAssignedLeasesAsync(
        IdentityEmailDeliveryDbContext context,
        IReadOnlyList<(Guid LeaseId, Guid DeliveryId)> assigned,
        CancellationToken cancellationToken)
    {
        if (assigned.Count == 0)
            return [];
        Guid[] ids = assigned.Select(item => item.DeliveryId).ToArray();
        Dictionary<Guid, Guid> leaseIds = assigned.ToDictionary(item => item.DeliveryId, item => item.LeaseId);
        List<IdentityEmailDeliveryRecord> rows = await context.EmailDeliveries.AsNoTracking()
            .Where(row => ids.Contains(row.Id)).ToListAsync(cancellationToken);
        return rows.Where(row => row.State == Leased && row.LeaseId == leaseIds[row.Id])
            .Select(ToLease).ToList();
    }

    private async Task<List<EmailDeliveryLease>> RecoverAssignedLeasesAsync(
        IReadOnlyList<(Guid LeaseId, Guid DeliveryId)> assigned,
        CancellationToken cancellationToken)
    {
        await using IdentityEmailDeliveryDbContext recovery = CreateContext();
        return await ReadAssignedLeasesAsync(recovery, assigned, cancellationToken);
    }

    private sealed record ClaimTransactionResult(
        List<EmailDeliveryLease> Leases,
        List<(Guid LeaseId, Guid DeliveryId)> Assigned,
        Exception? CommitFailure);

    private static EmailDeliveryLease ToLease(IdentityEmailDeliveryRecord row) => new(
        new EmailDeliveryProtectionContext(EmailDeliveryBoundary.Identity, row.UserId, row.Id, row.TemplateKey, row.TemplateVersion),
        row.LeaseId!.Value, row.Attempts, row.LeaseUntilUtc!.Value, row.ProtectedIntent);

    private static void ValidateForInsert(IdentityEmailDeliveryRecord record)
    {
        if (record.Id == Guid.Empty || record.UserId == Guid.Empty || string.IsNullOrWhiteSpace(record.TemplateKey)
            || record.TemplateKey.Length > 160 || record.TemplateVersion < 1
            || string.IsNullOrEmpty(record.ProtectedIntent) || record.ProtectedIntent.Length > 262144
            || record.State != Queued || record.Attempts != 0 || record.LeaseId is not null
            || record.LeaseUntilUtc is not null || record.LastCompletedLeaseId is not null
            || record.LastOutcome is not null || record.LastFailureCode is not null
            || record.LastSmtpStatusCode is not null || record.CompletedAtUtc is not null)
            throw new ArgumentException("Identity email delivery insert is invalid.", nameof(record));
    }

    private static string ToState(EmailDeliveryStatus status) => status switch
    {
        EmailDeliveryStatus.Queued => "queued",
        EmailDeliveryStatus.Leased => "leased",
        EmailDeliveryStatus.Delivered => "delivered",
        EmailDeliveryStatus.Suppressed => "suppressed",
        EmailDeliveryStatus.Terminal => "terminal",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };
}
