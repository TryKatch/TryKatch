using System.Text.Json;
using Trykatch.Application.Authorization;
using Trykatch.Application.Organizations;
using Trykatch.Modules.Projects.Application;
using Trykatch.Modules.Projects.IntegrationEvents;

namespace Trykatch.Modules.Projects.Infrastructure;

internal static class ProjectsRealtime
{
    internal const string Operation = "projects.publish-created-invalidation";
    internal const string Consumer = "projects.created-realtime";
    internal const string Grant = "projects.created-realtime";
    internal static readonly Guid AuditActor = Guid.Parse("13af09b6-8885-46bb-a25d-cf883d77502d");
    internal static readonly ModuleRealtimePolicyRef ReadPolicy = new("projects", "projects.read", 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static ProjectCreated? ReadSource(JsonElement payload, Guid organizationId, Guid? originActor)
    {
        if (payload.ValueKind != JsonValueKind.Object || organizationId == Guid.Empty || originActor is null || originActor == Guid.Empty)
            return null;
        try
        {
            ProjectCreated? source = payload.Deserialize<ProjectCreated>(Json);
            return source is not null && source.ProjectId != Guid.Empty && source.OrganizationId == organizationId
                && source.ActorId == originActor ? source : null;
        }
        catch (JsonException) { return null; }
    }
}

internal sealed class ProjectCreatedRealtimeConsumer(IModuleRealtimePublisher publisher) : IModuleEventConsumer
{
    public Task HandleAsync(ModuleEventContext context, JsonElement payload, CancellationToken cancellationToken)
    {
        ProjectCreated? source = ProjectsRealtime.ReadSource(payload, context.OrganizationId, context.OriginActorId);
        if (source is null || context.MessageId == Guid.Empty || context.EffectiveActorId != ProjectsRealtime.AuditActor
            || context.EventKey != "projects.created" || context.SchemaVersion != 1)
            throw new InvalidOperationException("Projects realtime requires a compatible committed source event.");
        return publisher.PublishAsync(new("projects", "projects.created", 1, context.MessageId,
            new(ProjectsRealtime.ReadPolicy, ModuleRealtimeAudienceKind.Organization,
                Resource: new("projects", source.ProjectId))), cancellationToken);
    }
}

internal sealed class ProjectsReadAudiencePolicy(
    IProjectStore store, IOrganizationContext organization, IPermissionAuthorizer permissions,
    Trykatch.Modules.IOrganizationFeatureEvaluator featureEvaluator) : IModuleRealtimeAudiencePolicy
{
    public async Task<bool> AuthorizeAsync(ModuleRealtimeAudienceContext context, ModuleRealtimeAudience audience,
        CancellationToken cancellationToken)
    {
        if (!organization.IsResolved || context.OrganizationId != organization.OrganizationId
            || context.ActorId == Guid.Empty || context.ActorId != organization.ActorId
            || audience.Policy != ProjectsRealtime.ReadPolicy || !Enum.IsDefined(audience.Kind)
            || (audience.Kind == ModuleRealtimeAudienceKind.User ? audience.UserId != context.ActorId : audience.UserId is not null)
            || !await permissions.HasPermissionAsync(Permissions.ProjectsRead, cancellationToken)) return false;
        if (!await featureEvaluator.IsEnabledAsync(ProjectFeatures.Reads, cancellationToken)) return false;
        if (audience.Resource is null) return audience.Kind != ModuleRealtimeAudienceKind.Resource;
        // Use the same current store visibility as GetAsync, including readable lifecycle metadata.
        return audience.Resource.ResourceKey == "projects" && audience.Resource.ResourceId != Guid.Empty
            && await store.FindAsync(context.OrganizationId, audience.Resource.ResourceId, cancellationToken) is not null;
    }
}
