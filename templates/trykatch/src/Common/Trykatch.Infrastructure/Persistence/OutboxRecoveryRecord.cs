namespace Trykatch.Infrastructure.Persistence;

public sealed class OutboxReplayRequest
{
    public Guid RequestId { get; init; }
    public Guid MessageId { get; init; }
    public int ExpectedFailedGeneration { get; init; }
    public Guid ActorId { get; init; }
    public DateTimeOffset RequestedAt { get; init; }
}

public sealed class OutboxRecoveryEvent
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid MessageId { get; init; }
    public int ReplayGeneration { get; init; }
    public string Outcome { get; init; } = string.Empty;
    public string? FailureCode { get; init; }
    public string? FailureType { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public Guid? RequestId { get; init; }
    public Guid? ActorId { get; init; }
}
