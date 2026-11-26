namespace Trykatch.Modules.Projects.IntegrationEvents;

public sealed record ProjectCreated(Guid OrganizationId, Guid ProjectId, Guid ActorId);
