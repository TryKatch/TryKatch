using FlatpackApp.Domain.Common;

namespace FlatpackApp.Domain.Projects;

public sealed class Project : RecoverableEntity
{
    private Project(Guid id, Guid organizationId, string name, string description, Guid createdBy) : base(id)
    {
        OrganizationId = organizationId;
        Name = name;
        Description = description;
        CreatedBy = createdBy;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    private Project() : base(Guid.Empty) { }

    public Guid OrganizationId { get; private init; }
    public string Name { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public Guid CreatedBy { get; private init; }
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    public static Project Create(Guid organizationId, string name, string? description, Guid createdBy)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("Project name is required.");
        }

        return new Project(Guid.CreateVersion7(), organizationId, name.Trim(), description?.Trim() ?? string.Empty, createdBy);
    }

    public void Update(string name, string? description)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("Project name is required.");
        }

        Name = name.Trim();
        Description = description?.Trim() ?? string.Empty;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public bool Archive(Guid actorId, DateTimeOffset now) => MarkArchived(actorId, now);
    public bool Restore() => MarkRestored();
    public bool Delete(Guid actorId, string reason, DateTimeOffset now) => MarkArchivedAsDeleted(actorId, reason, now);
}
