using Trykatch.Domain.Common;

namespace Trykatch.Domain.Organizations;

public sealed class Membership : RecoverableEntity
{
    private Membership(Guid id, Guid organizationId, Guid userId) : base(id)
    {
        OrganizationId = organizationId;
        UserId = userId;
        JoinedAt = DateTimeOffset.UtcNow;
    }

    private Membership() : base(Guid.Empty) { }

    public Guid OrganizationId { get; private init; }
    public Guid UserId { get; private init; }
    public MembershipStatus Status { get; private set; } = MembershipStatus.Active;
    public DateTimeOffset JoinedAt { get; private init; }
    public ICollection<MembershipRole> Roles { get; private set; } = [];

    public static Membership Create(Guid organizationId, Guid userId) =>
        new(Guid.CreateVersion7(), organizationId, userId);

    public void AssignRole(Guid roleId)
    {
        if (Roles.All(x => x.RoleId != roleId))
        {
            Roles.Add(new MembershipRole(Id, roleId));
        }
    }

    public void SetRoles(IEnumerable<Guid> roleIds)
    {
        Roles.Clear();
        foreach (Guid roleId in roleIds.Distinct())
        {
            Roles.Add(new MembershipRole(Id, roleId));
        }
    }

    public void Suspend() => Status = MembershipStatus.Suspended;
    public void Activate() => Status = MembershipStatus.Active;
    public bool Archive(Guid actorId, DateTimeOffset now) => MarkArchived(actorId, now);
    public bool Restore() => MarkRestored();
    public bool Delete(Guid actorId, string reason, DateTimeOffset now) => MarkArchivedAsDeleted(actorId, reason, now);
}

public enum MembershipStatus
{
    Active = 1,
    Suspended = 2
}

public sealed class MembershipRole
{
    private MembershipRole() { }

    public MembershipRole(Guid membershipId, Guid roleId)
    {
        MembershipId = membershipId;
        RoleId = roleId;
    }

    public Guid MembershipId { get; private init; }
    public Guid RoleId { get; private init; }
}
