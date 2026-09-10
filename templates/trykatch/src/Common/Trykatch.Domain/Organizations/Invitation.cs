using Trykatch.Domain.Common;

namespace Trykatch.Domain.Organizations;

public sealed class Invitation : RecoverableEntity
{
    private Invitation(Guid id, Guid organizationId, Guid roleId, string email, string tokenHash, DateTimeOffset expiresAt) : base(id)
    {
        OrganizationId = organizationId;
        RoleId = roleId;
        Email = email;
        TokenHash = tokenHash;
        ExpiresAt = expiresAt;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    private Invitation() : base(Guid.Empty) { }

    public Guid OrganizationId { get; private init; }
    public Guid RoleId { get; private init; }
    public string Email { get; private init; } = string.Empty;
    public string TokenHash { get; private init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? AcceptedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsUsable(DateTimeOffset now) => LifecycleState == RecordLifecycleState.Active && AcceptedAt is null && RevokedAt is null && ExpiresAt > now;

    public static Invitation Create(Guid organizationId, Guid roleId, string email, string tokenHash, DateTimeOffset expiresAt) =>
        new(Guid.CreateVersion7(), organizationId, roleId, email.Trim().ToLowerInvariant(), tokenHash, expiresAt);

    public void Accept(DateTimeOffset now)
    {
        if (!IsUsable(now))
        {
            throw new DomainException("Invitation is no longer usable.");
        }

        AcceptedAt = now;
    }

    public void Revoke(DateTimeOffset now)
    {
        if (AcceptedAt is not null)
        {
            throw new DomainException("An accepted invitation cannot be revoked.");
        }

        RevokedAt ??= now;
    }

    public void Reschedule(DateTimeOffset now, DateTimeOffset expiresAt)
    {
        if (!IsUsable(now))
        {
            throw new DomainException("Only a pending invitation can be rescheduled.");
        }

        if (expiresAt <= now)
        {
            throw new DomainException("Invitation expiry must be in the future.");
        }

        ExpiresAt = expiresAt;
    }

    public bool Restore() => MarkRestored();
    public bool Delete(Guid actorId, string reason, DateTimeOffset now) => MarkDeleted(actorId, reason, now);
}
