using __ROOT_NAMESPACE__.Modules;

namespace __ROOT_NAMESPACE__.Modules.__MODULE__.Domain;

public enum __ENTITY__LifecycleState { Active = 1, Archived = 2, Deleted = 3 }

public sealed class __ENTITY__Record : IOrganizationOwned
{
    private __ENTITY__Record() { }

    private __ENTITY__Record(Guid organizationId, Guid actorId, string name, string? description, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7();
        OrganizationId = organizationId;
        CreatedBy = actorId;
        Name = name.Trim();
        Description = description?.Trim();
        CreatedAt = now;
    }

    public Guid Id { get; private init; }
    public Guid OrganizationId { get; private init; }
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public Guid CreatedBy { get; private init; }
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    public DateTimeOffset? ArchivedAt { get; private set; }
    public Guid? ArchivedBy { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public Guid? DeletedBy { get; private set; }
    public string? DeletionReason { get; private set; }

    public __ENTITY__LifecycleState LifecycleState => DeletedAt is not null
        ? __ENTITY__LifecycleState.Deleted
        : ArchivedAt is not null ? __ENTITY__LifecycleState.Archived : __ENTITY__LifecycleState.Active;

    public static __ENTITY__Record Create(Guid organizationId, Guid actorId, string name, string? description, DateTimeOffset now) =>
        new(organizationId, actorId, name, description, now);

    public void Update(string name, string? description, DateTimeOffset now)
    {
        if (LifecycleState != __ENTITY__LifecycleState.Active)
            throw new InvalidOperationException("Restore the record before editing it.");
        Name = name.Trim();
        Description = description?.Trim();
        UpdatedAt = now;
    }

    public bool Archive(Guid actorId, DateTimeOffset now)
    {
        if (DeletedAt is not null) throw new InvalidOperationException("A deleted record cannot be archived.");
        if (ArchivedAt is not null) return false;
        ArchivedAt = now;
        ArchivedBy = actorId;
        return true;
    }

    public bool Restore()
    {
        if (LifecycleState == __ENTITY__LifecycleState.Active) return false;
        ArchivedAt = null;
        ArchivedBy = null;
        DeletedAt = null;
        DeletedBy = null;
        DeletionReason = null;
        return true;
    }

    public bool RequestDeletion(Guid actorId, string reason, DateTimeOffset now)
    {
        if (LifecycleState != __ENTITY__LifecycleState.Archived)
            throw new InvalidOperationException("The record must be archived before deletion can be requested.");
        string normalizedReason = reason.Trim();
        if (normalizedReason.Length is < 10 or > 500)
            throw new ArgumentException("A deletion reason containing 10-500 characters is required.", nameof(reason));
        if (DeletedAt is not null) return false;
        DeletedAt = now;
        DeletedBy = actorId;
        DeletionReason = normalizedReason;
        return true;
    }
}
