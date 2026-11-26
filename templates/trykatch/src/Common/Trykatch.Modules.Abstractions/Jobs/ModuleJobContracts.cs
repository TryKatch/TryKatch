using System.Text.Json;

namespace Trykatch.Modules;

/// <summary>A registered job payload and the operation that executes it.</summary>
public sealed record ModuleJobDescriptor(
    string ModuleId,
    string JobKey,
    int JobVersion,
    string OperationKey,
    int OperationVersion,
    ModuleExecutionKind ExecutionKind,
    string PayloadSchemaKey,
    int PayloadSchemaVersion,
    int MaximumPayloadBytes);

/// <summary>A caller's idempotent request to enqueue or schedule module work.</summary>
public sealed record ModuleJobRequest(Guid RequestId, string JobKey, int JobVersion, JsonElement Payload);

public enum ModuleWorkState
{
    Accepted,
    Scheduled,
    AlreadyAccepted,
    Rejected,
    Unavailable
}

/// <summary>A durable acknowledgement of accepted module work.</summary>
public sealed record ModuleWorkReceipt(
    Guid WorkId,
    ModuleWorkState State,
    DateTimeOffset? AcceptedAtUtc = null,
    string? FailureCode = null);

public enum ModuleMisfirePolicy
{
    Coalesce,
    CatchUp
}

/// <summary>A reviewed recurring schedule tied to one registered module job.</summary>
public sealed record ModuleRecurringScheduleDescriptor(
    string ScheduleKey,
    string OwnerModuleId,
    string JobKey,
    int JobVersion,
    string UtcSchedule,
    ModuleMisfirePolicy MisfirePolicy,
    string SystemGrantKey);

/// <summary>Host-provided durable submission seam for a module's current request.</summary>
public interface IModuleJobScheduler
{
    Task<ModuleWorkReceipt> EnqueueAsync(ModuleJobRequest request, CancellationToken cancellationToken = default);

    Task<ModuleWorkReceipt> ScheduleAsync(
        ModuleJobRequest request,
        DateTimeOffset dueAtUtc,
        CancellationToken cancellationToken = default);
}
