using Trykatch.Domain.Common;
using Trykatch.Modules;

namespace Trykatch.Domain.Organizations;

public sealed class AuditEntry : Entity, IOrganizationOwned
{
    private AuditEntry() : base(Guid.Empty) { }

    public AuditEntry(Guid organizationId, Guid actorId, string action, string subjectType, string subjectId, string subjectDisplayName, string details)
        : this(Guid.CreateVersion7(), organizationId, actorId, action, subjectType, subjectId, subjectDisplayName, details, DateTimeOffset.UtcNow)
    {
    }

    public AuditEntry(Guid id, Guid organizationId, Guid actorId, string action, string subjectType, string subjectId, string subjectDisplayName, string details, DateTimeOffset occurredAt)
        : base(id)
    {
        OrganizationId = organizationId;
        ActorId = actorId;
        Action = action;
        SubjectType = subjectType;
        SubjectId = subjectId;
        SubjectDisplayName = subjectDisplayName;
        Details = details;
        OccurredAt = occurredAt;
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
