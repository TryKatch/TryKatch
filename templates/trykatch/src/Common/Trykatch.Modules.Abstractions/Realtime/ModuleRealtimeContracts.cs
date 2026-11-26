namespace Trykatch.Modules;

public enum ModuleRealtimeAudienceKind
{
    Organization,
    User,
    Resource
}

public sealed record ModuleRealtimePolicyRef(string OwnerModuleId, string PolicyKey, int PolicyVersion);

public sealed record ModuleRealtimeResourceRef(string ResourceKey, Guid ResourceId)
{
    public override string ToString() => $"Realtime resource {ResourceKey}";
}

public sealed record ModuleRealtimeAudience(
    ModuleRealtimePolicyRef Policy,
    ModuleRealtimeAudienceKind Kind,
    Guid? UserId = null,
    ModuleRealtimeResourceRef? Resource = null)
{
    public override string ToString() => $"Realtime audience {Kind}";
}

public sealed record ModuleRealtimeNotification(
    string ModuleId,
    string EventKey,
    int SchemaVersion,
    Guid EventId,
    ModuleRealtimeAudience Audience)
{
    public override string ToString() => $"Realtime invalidation {ModuleId}/{EventKey}";
}

public interface IModuleRealtimePublisher
{
    Task PublishAsync(ModuleRealtimeNotification notification, CancellationToken cancellationToken = default);
}

public sealed record ModuleRealtimeProducerRef(string ModuleId, string OperationKey, int OperationVersion);

public sealed record ModuleRealtimeAudiencePolicyDescriptor(
    string OwnerModuleId,
    string PolicyKey,
    int PolicyVersion,
    IReadOnlyList<ModuleRealtimeAudienceKind> AllowedAudiences,
    IReadOnlyList<string> AllowedResourceKeys,
    IReadOnlyList<string> RequiredUserPermissionKeys,
    IReadOnlyList<ModuleEventVersion> AllowedEvents,
    IReadOnlyList<ModuleRealtimeProducerRef> AllowedSystemProducers);

public sealed record ModuleRealtimeAudienceContext(Guid OrganizationId, Guid ActorId)
{
    public override string ToString() => "Realtime audience authorization context";
}

public interface IModuleRealtimeAudiencePolicy
{
    Task<bool> AuthorizeAsync(
        ModuleRealtimeAudienceContext context,
        ModuleRealtimeAudience audience,
        CancellationToken cancellationToken);
}

public sealed record ModuleRealtimeAudiencePolicyBinding(
    ModuleRealtimePolicyRef Policy,
    IModuleRealtimeAudiencePolicy Adapter);
