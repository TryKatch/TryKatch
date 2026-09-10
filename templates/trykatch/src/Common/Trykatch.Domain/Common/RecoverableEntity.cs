namespace Trykatch.Domain.Common;

/// <summary>
/// Provides a reversible record lifecycle without coupling domain aggregates to persistence.
/// Purging is deliberately not part of the application lifecycle.
/// </summary>
public abstract class RecoverableEntity : Entity
{
    protected RecoverableEntity(Guid id) : base(id) { }

    public DateTimeOffset? ArchivedAt { get; private set; }
    public Guid? ArchivedBy { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public Guid? DeletedBy { get; private set; }
    public string? DeletionReason { get; private set; }

    public RecordLifecycleState LifecycleState => DeletedAt is not null
        ? RecordLifecycleState.Deleted
        : ArchivedAt is not null
            ? RecordLifecycleState.Archived
            : RecordLifecycleState.Active;

    protected bool MarkArchived(Guid actorId, DateTimeOffset now)
    {
        if (DeletedAt is not null)
        {
            throw new DomainException("A deleted record cannot be archived. Restore it first.");
        }

        if (ArchivedAt is not null) return false;
        ArchivedAt = now;
        ArchivedBy = actorId;
        return true;
    }

    protected bool MarkRestored()
    {
        if (LifecycleState == RecordLifecycleState.Active) return false;
        ArchivedAt = null;
        ArchivedBy = null;
        DeletedAt = null;
        DeletedBy = null;
        DeletionReason = null;
        return true;
    }

    protected bool MarkDeleted(Guid actorId, string reason, DateTimeOffset now)
    {
        string normalizedReason = reason?.Trim() ?? string.Empty;
        if (normalizedReason.Length is < 10 or > 500)
        {
            throw new DomainException("A deletion reason containing 10-500 characters is required.");
        }

        if (DeletedAt is not null) return false;
        DeletedAt = now;
        DeletedBy = actorId;
        DeletionReason = normalizedReason;
        return true;
    }

    protected bool MarkArchivedAsDeleted(Guid actorId, string reason, DateTimeOffset now)
    {
        if (LifecycleState != RecordLifecycleState.Archived)
        {
            throw new DomainException("The record must be archived before deletion can be requested.");
        }

        return MarkDeleted(actorId, reason, now);
    }
}

public enum RecordLifecycleState
{
    Active = 1,
    Archived = 2,
    Deleted = 3
}
