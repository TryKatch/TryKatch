namespace Trykatch.Identity;

internal sealed class IdentitySessionRecord
{
    public required string SessionHash { get; init; }
    public Guid UserId { get; init; }
    public required string SecurityStamp { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid WorkspaceGeneration { get; set; }
}
