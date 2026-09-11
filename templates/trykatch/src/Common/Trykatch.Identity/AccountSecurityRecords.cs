namespace Trykatch.Identity;

internal sealed class RecentAssuranceRecord
{
    public required string TokenHash { get; set; }
    public Guid UserId { get; set; }
    public required string SessionId { get; set; }
    public required string SecurityStamp { get; set; }
    public required string Purpose { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}

internal sealed class PendingMfaEnrollment
{
    public Guid UserId { get; set; }
    public Guid EnrollmentId { get; set; }
    public required string SessionId { get; set; }
    public required string SecurityStamp { get; set; }
    public required string ProtectedKey { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int FailedAttempts { get; set; }
}

internal sealed class AccountSecurityEvent
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public required string Action { get; set; }
    public required string Outcome { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
