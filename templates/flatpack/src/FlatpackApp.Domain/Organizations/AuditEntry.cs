using FlatpackApp.Domain.Common;

namespace FlatpackApp.Domain.Organizations;

public sealed class AuditEntry : Entity
{
    private AuditEntry() : base(Guid.Empty) { }

    public AuditEntry(Guid organizationId, Guid actorId, string action, string subjectType, string subjectId, string subjectDisplayName, string details)
        : base(Guid.CreateVersion7())
    {
        OrganizationId = organizationId;
        ActorId = actorId;
        Action = action;
        SubjectType = subjectType;
        SubjectId = subjectId;
        SubjectDisplayName = subjectDisplayName;
        Details = details;
        OccurredAt = DateTimeOffset.UtcNow;
    }

    public Guid OrganizationId { get; private init; }
    public Guid ActorId { get; private init; }
    public string Action { get; private init; } = string.Empty;
    public string SubjectType { get; private init; } = string.Empty;
    public string SubjectId { get; private init; } = string.Empty;
    public string SubjectDisplayName { get; private init; } = string.Empty;
    public string Details { get; private init; } = "{}";
    public DateTimeOffset OccurredAt { get; private init; }
}
