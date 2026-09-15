using __ROOT_NAMESPACE__.Modules;

namespace __ROOT_NAMESPACE__.Modules.__MODULE__.Domain;

public enum __ENTITY__LifecycleState { Active = 1, Archived = 2, Deleted = 3 }

__FIELD_ENUMS__

public sealed partial class __ENTITY__Record : IOrganizationOwned
{
    private __ENTITY__Record() { }

    private __ENTITY__Record(Guid organizationId, Guid actorId,
        __DOMAIN_FIELD_PARAMETERS__,
        DateTimeOffset now)
    {
        ValidateBusinessFields(__DOMAIN_FIELD_ARGUMENTS__);
        if (organizationId == Guid.Empty || actorId == Guid.Empty) throw new BlueprintRuleException("identity_required", "identity", "Organization and actor are required.");
        Id = Guid.CreateVersion7();
        OrganizationId = organizationId;
        CreatedBy = actorId;
        __DOMAIN_FIELD_ASSIGNMENTS__
        CreatedAt = now;
    }

    public Guid Id { get; private init; }
    public Guid OrganizationId { get; private init; }
    __DOMAIN_FIELD_PROPERTIES__
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

    public static __ENTITY__Record Create(Guid organizationId, Guid actorId,
        __DOMAIN_FIELD_PARAMETERS__,
        DateTimeOffset now) =>
        new(organizationId, actorId, __DOMAIN_FIELD_ARGUMENTS__, now);

    public void Update(
        __DOMAIN_FIELD_PARAMETERS__,
        DateTimeOffset now)
    {
        if (LifecycleState != __ENTITY__LifecycleState.Active)
            throw new InvalidOperationException("Restore the record before editing it.");
        EnsureEditable();
        ValidateBusinessFields(__DOMAIN_FIELD_ARGUMENTS__);
        __DOMAIN_FIELD_ASSIGNMENTS__
        UpdatedAt = now;
        Version = Guid.NewGuid();
    }

    public bool Archive(Guid actorId, DateTimeOffset now)
    {
        if (DeletedAt is not null) throw new InvalidOperationException("A deleted record cannot be archived.");
        if (ArchivedAt is not null) return false;
        Version = Guid.NewGuid();
        ArchivedAt = now;
        ArchivedBy = actorId;
        return true;
    }

    public bool Restore()
    {
        if (LifecycleState == __ENTITY__LifecycleState.Active) return false;
        Version = Guid.NewGuid();
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
        Version = Guid.NewGuid();
        DeletedAt = now;
        DeletedBy = actorId;
        DeletionReason = normalizedReason;
        return true;
    }
}
