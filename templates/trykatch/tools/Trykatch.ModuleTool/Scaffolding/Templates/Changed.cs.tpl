namespace __ROOT_NAMESPACE__.Modules.__MODULE__.IntegrationEvents;

public sealed record __ENTITY__Changed(
    Guid __ENTITY__Id,
    Guid OrganizationId,
    string Operation,
    Guid ActorId,
    DateTimeOffset OccurredAt,
    string? Reason = null);
