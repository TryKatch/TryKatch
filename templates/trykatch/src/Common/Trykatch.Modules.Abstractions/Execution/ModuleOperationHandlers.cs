using System.Text.Json;

namespace Trykatch.Modules;

/// <summary>Trusted host context for one invocation of a registered module operation.</summary>
public sealed record ModuleOperationContext(
    Guid WorkId,
    Guid OrganizationId,
    Guid EffectiveActorId,
    Guid? OriginActorId,
    ModuleExecutionKind ExecutionKind,
    string OperationKey,
    int OperationVersion);

/// <summary>Explicit module-owned operation implementation selected by the host registry.</summary>
public interface IModuleOperationHandler
{
    string ModuleId { get; }
    string OperationKey { get; }
    int OperationVersion { get; }

    Task HandleAsync(ModuleOperationContext context, JsonElement payload, CancellationToken cancellationToken);
}

/// <summary>Current resource authorization required before a registered operation is invoked.</summary>
public interface IModuleOperationResourcePolicy
{
    string ModuleId { get; }
    string PolicyKey { get; }

    Task<bool> AuthorizeAsync(ModuleOperationContext context, JsonElement payload, CancellationToken cancellationToken);
}
