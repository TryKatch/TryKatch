using Trykatch.Application.Common;

namespace Trykatch.Application.Outbox;

public sealed record OutboxFailure(
    Guid MessageId,
    int ReplayGeneration,
    string FailureCode,
    string FailureType,
    DateTimeOffset ExhaustedAt);

public sealed record RequestOutboxReplay(Guid RequestId, Guid MessageId, int ExpectedFailedGeneration);
public sealed record OutboxReplayAccepted(Guid RequestId, Guid MessageId, int ExpectedFailedGeneration, DateTimeOffset RequestedAt);
public sealed record OutboxReplayOutcome(Guid RequestId, Guid MessageId, int ReplayGeneration, string Outcome, DateTimeOffset OccurredAt);
public sealed record OutboxReplayPending(Guid RequestId, string Outcome);

public interface IOutboxRecoveryDirectory
{
    Task<Result<PagedResult<OutboxFailure>>> ListFailuresAsync(Guid actorId, int page, int pageSize, CancellationToken cancellationToken = default);
    Task<Result<OutboxReplayAccepted>> RequestReplayAsync(Guid actorId, RequestOutboxReplay request, CancellationToken cancellationToken = default);
    Task<Result<OutboxReplayOutcome>> GetReplayOutcomeAsync(Guid actorId, Guid requestId, CancellationToken cancellationToken = default);
}
