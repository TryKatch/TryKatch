namespace __ROOT_NAMESPACE__.Modules.__MODULE__.IntegrationEvents;

public sealed record __ENTITY__Created(
    Guid __ENTITY__Id,
    Guid OrganizationId,
    Guid ActorId,
    DateTimeOffset OccurredAt);

public sealed record __ENTITY__Updated(
    Guid __ENTITY__Id,
    Guid OrganizationId,
    Guid ActorId,
    DateTimeOffset OccurredAt);

public sealed record __ENTITY__Archived(
    Guid __ENTITY__Id,
    Guid OrganizationId,
    Guid ActorId,
    DateTimeOffset OccurredAt);

public sealed record __ENTITY__Restored(
    Guid __ENTITY__Id,
    Guid OrganizationId,
    Guid ActorId,
    DateTimeOffset OccurredAt);

public sealed record __ENTITY__DeletionRequested(
    Guid __ENTITY__Id,
    Guid OrganizationId,
    Guid ActorId,
    DateTimeOffset OccurredAt,
    string Reason);

__ACTION_EVENTS__

