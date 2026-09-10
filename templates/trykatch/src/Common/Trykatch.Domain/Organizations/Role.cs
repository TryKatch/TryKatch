using Trykatch.Domain.Common;

namespace Trykatch.Domain.Organizations;

public sealed class Role : RecoverableEntity
{
    private Role(Guid id, Guid organizationId, string name, string description, bool isSystem) : base(id)
    {
        OrganizationId = organizationId;
        Name = name;
        Description = description;
        IsSystem = isSystem;
    }

    private Role() : base(Guid.Empty) { }

    public Guid OrganizationId { get; private init; }
    public string Name { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public bool IsSystem { get; private init; }
    public ICollection<RolePermissionGrant> Permissions { get; private set; } = [];

    public static Role Create(Guid organizationId, string name, string description = "", bool isSystem = false)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("Role name is required.");
        }

        if (description.Trim().Length > 240)
        {
            throw new DomainException("Role description cannot exceed 240 characters.");
        }

        return new Role(Guid.CreateVersion7(), organizationId, name.Trim(), description.Trim(), isSystem);
    }

    public void Rename(string name)
    {
        if (IsSystem)
        {
            throw new DomainException("System roles cannot be renamed.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("Role name is required.");
        }

        Name = name.Trim();
    }

    public void Describe(string description)
    {
        if (IsSystem)
        {
            throw new DomainException("System roles cannot be changed.");
        }

        if (description.Trim().Length > 240)
        {
            throw new DomainException("Role description cannot exceed 240 characters.");
        }

        Description = description.Trim();
    }

    public void SetPermissions(IEnumerable<string> permissions)
    {
        Permissions.Clear();
        foreach (string permission in permissions.Distinct(StringComparer.Ordinal))
        {
            Permissions.Add(new RolePermissionGrant(Id, permission));
        }
    }

    /// <summary>
    /// Adds newly introduced module defaults without taking away grants that an
    /// existing installation already holds. This makes module activation additive
    /// and idempotent across upgrades.
    /// </summary>
    public void AddMissingPermissions(IEnumerable<string> permissions)
    {
        HashSet<string> existing = Permissions.Select(grant => grant.Permission).ToHashSet(StringComparer.Ordinal);
        foreach (string permission in permissions.Distinct(StringComparer.Ordinal))
        {
            if (existing.Add(permission))
                Permissions.Add(new RolePermissionGrant(Id, permission));
        }
    }

    public bool Archive(Guid actorId, DateTimeOffset now)
    {
        EnsureCustomRole("archived");
        return MarkArchived(actorId, now);
    }

    public bool Restore()
    {
        EnsureCustomRole("restored");
        return MarkRestored();
    }

    public bool Delete(Guid actorId, string reason, DateTimeOffset now)
    {
        EnsureCustomRole("deleted");
        return MarkArchivedAsDeleted(actorId, reason, now);
    }

    private void EnsureCustomRole(string operation)
    {
        if (IsSystem)
        {
            throw new DomainException($"System roles cannot be {operation}.");
        }
    }
}

public sealed class RolePermissionGrant
{
    private RolePermissionGrant() { }

    public RolePermissionGrant(Guid roleId, string permission)
    {
        RoleId = roleId;
        Permission = permission;
    }

    public Guid RoleId { get; private init; }
    public string Permission { get; private init; } = string.Empty;
}
