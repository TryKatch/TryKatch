namespace Trykatch.Application.Outbox;

/// <summary>Host-captured provenance persisted beside a versioned outbox payload.</summary>
public sealed record OutboxContext(
    Guid OrganizationId,
    Guid EffectiveActorId,
    Guid OriginActorId,
    Guid MembershipId,
    string ExecutionKind,
    string OperationKey,
    int OperationVersion,
    string EventKey,
    int SchemaVersion,
    string CatalogVersion,
    string? CorrelationId);
