namespace Trykatch.Modules.Documents.IntegrationEvents;

public sealed record DocumentChanged(
    Guid DocumentId,
    Guid OrganizationId,
    string Operation,
    Guid ActorId,
    DateTimeOffset OccurredAt,
    string? Reason = null);
