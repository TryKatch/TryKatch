namespace Trykatch.Modules;

/// <summary>How the host derives the authority for one registered operation.</summary>
public enum ModuleExecutionKind
{
    UserCommand,
    CommittedEvent,
    ScheduledSystem
}

/// <summary>An explicit module-owned operation accepted by the host executor.</summary>
public sealed record ModuleOperationDescriptor(
    string ModuleId,
    string OperationKey,
    int OperationVersion,
    ModuleExecutionKind ExecutionKind,
    string PayloadSchemaKey,
    int PayloadSchemaVersion,
    IReadOnlyList<string> RequiredPermissionKeys,
    string ResourcePolicyKey,
    string? SystemGrantKey,
    int MaximumPayloadBytes);

/// <summary>A finite set of system authority for operations and module resources.</summary>
public sealed record ModuleSystemGrantDescriptor(
    string GrantKey,
    int GrantVersion,
    string OwnerModuleId,
    Guid AuditActorId,
    IReadOnlyList<string> AllowedOperationKeys,
    IReadOnlyList<string> AllowedResourceKeys)
{
    /// <summary>Email-specific source-policy authority; this does not widen business-resource grants.</summary>
    public IReadOnlyList<ModuleEmailDisclosurePolicyRef> AllowedEmailDisclosurePolicies { get; init; } = [];

    /// <summary>Realtime audience policies this producer grant may publish for.</summary>
    public IReadOnlyList<ModuleRealtimePolicyRef> AllowedRealtimePolicies { get; init; } = [];
}

/// <summary>Persisted execution provenance; it is data, not an authorization token.</summary>
public sealed record ModuleExecutionIdentity(
    Guid OrganizationId,
    Guid EffectiveActorId,
    Guid? OriginActorId,
    Guid? MembershipId,
    ModuleExecutionKind ExecutionKind,
    string? GrantKey,
    int? GrantVersion,
    Guid? SourceMessageId,
    int CatalogVersion,
    string? TraceCorrelation);
