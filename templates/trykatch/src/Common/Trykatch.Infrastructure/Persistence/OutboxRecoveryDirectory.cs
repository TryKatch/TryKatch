using Microsoft.EntityFrameworkCore;
using Trykatch.Application.Common;
using Trykatch.Application.Identity;
using Trykatch.Application.Outbox;

namespace Trykatch.Infrastructure.Persistence;

internal sealed class OutboxRecoveryDirectory(
    PlatformDbContext dbContext,
    IPlatformAccessDirectory accessDirectory) : IOutboxRecoveryDirectory
{
    public async Task<Result<PagedResult<OutboxFailure>>> ListFailuresAsync(
        Guid actorId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        if (!await HasAsync(actorId, PlatformPermissions.OutboxRead, cancellationToken))
            return Result.Failure<PagedResult<OutboxFailure>>("forbidden", "Current platform access cannot read outbox failures.");
        int boundedPage = Math.Max(1, page);
        int boundedSize = Math.Clamp(pageSize, 1, 100);
        IQueryable<OutboxRecoveryEvent> terminal = dbContext.OutboxRecoveryEvents.AsNoTracking()
            .Where(x => x.Outcome == "terminal"
                && !dbContext.OutboxRecoveryEvents.Any(replay => replay.MessageId == x.MessageId
                    && replay.Outcome == "replayed"
                    && replay.ReplayGeneration == x.ReplayGeneration + 1));
        long total = await terminal.LongCountAsync(cancellationToken);
        OutboxFailure[] items = await terminal
            .OrderBy(x => x.OccurredAt).ThenBy(x => x.MessageId).ThenBy(x => x.ReplayGeneration)
            .Skip((boundedPage - 1) * boundedSize).Take(boundedSize)
            .Select(x => new OutboxFailure(x.MessageId, x.ReplayGeneration,
                x.FailureCode ?? "unclassified", x.FailureType ?? "unknown", x.OccurredAt))
            .ToArrayAsync(cancellationToken);
        return Result.Success(new PagedResult<OutboxFailure>(items, boundedPage, boundedSize, total));
    }

    public async Task<Result<OutboxReplayAccepted>> RequestReplayAsync(
        Guid actorId, RequestOutboxReplay request, CancellationToken cancellationToken = default)
    {
        if (request.RequestId == Guid.Empty || request.MessageId == Guid.Empty || request.ExpectedFailedGeneration < 0)
            return Result.Failure<OutboxReplayAccepted>("invalid_request", "Replay request identifiers and generation are invalid.");
        if (!await HasAsync(actorId, PlatformPermissions.OutboxReplay, cancellationToken))
            return Result.Failure<OutboxReplayAccepted>("forbidden", "Current platform access cannot request outbox replay.");

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO platform.outbox_replay_requests
              ("RequestId", "MessageId", "ExpectedFailedGeneration", "ActorId", "RequestedAt")
            VALUES ({request.RequestId}, {request.MessageId}, {request.ExpectedFailedGeneration}, {actorId}, CURRENT_TIMESTAMP)
            ON CONFLICT ("RequestId") DO NOTHING
            """, cancellationToken);
        OutboxReplayRequest stored = await dbContext.OutboxReplayRequests.AsNoTracking()
            .SingleAsync(x => x.RequestId == request.RequestId, cancellationToken);
        if (stored.ActorId != actorId || stored.MessageId != request.MessageId
            || stored.ExpectedFailedGeneration != request.ExpectedFailedGeneration)
            return Result.Failure<OutboxReplayAccepted>("request_conflict", "Replay request ID is already bound to another message generation.");
        return Result.Success(new OutboxReplayAccepted(stored.RequestId, stored.MessageId,
            stored.ExpectedFailedGeneration, stored.RequestedAt));
    }

    public async Task<Result<OutboxReplayOutcome>> GetReplayOutcomeAsync(
        Guid actorId, Guid requestId, CancellationToken cancellationToken = default)
    {
        if (!await HasAsync(actorId, PlatformPermissions.OutboxRead, cancellationToken))
            return Result.Failure<OutboxReplayOutcome>("forbidden", "Current platform access cannot read outbox replay outcomes.");
        OutboxRecoveryEvent? outcome = await dbContext.OutboxRecoveryEvents.AsNoTracking()
            .SingleOrDefaultAsync(x => x.RequestId == requestId, cancellationToken);
        return outcome is null
            ? Result.Failure<OutboxReplayOutcome>("pending", "Replay request has not been processed.")
            : Result.Success(new OutboxReplayOutcome(requestId, outcome.MessageId, outcome.ReplayGeneration,
                outcome.Outcome, outcome.OccurredAt));
    }

    private async Task<bool> HasAsync(Guid actorId, string permission, CancellationToken cancellationToken)
    {
        EffectivePlatformAccess access = await accessDirectory.ResolveEffectiveAccessAsync(actorId, cancellationToken);
        return access.HasPermission(permission);
    }
}
