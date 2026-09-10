namespace Trykatch.Modules.Projects.IntegrationEvents;

public sealed record ProjectChanged(Guid OrganizationId, Guid ProjectId, string Change, Guid ActorId);
