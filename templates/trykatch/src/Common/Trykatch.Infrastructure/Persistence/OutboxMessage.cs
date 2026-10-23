namespace Trykatch.Infrastructure.Persistence;

public sealed class OutboxMessage
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public string Type { get; init; } = string.Empty;
    public string Payload { get; init; } = string.Empty;
    public int? ContextVersion { get; init; }
    public string? ContextJson { get; init; }
    public string? PayloadHash { get; init; }
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorType { get; set; }
    public DateTimeOffset? ExhaustedAt { get; set; }
    public int ReplayGeneration { get; set; }
}
