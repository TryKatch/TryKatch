using Quartz;

namespace Trykatch.Infrastructure.Jobs;

internal static class ModuleQuartzKeys
{
    public const string WorkGroup = "trykatch-module-work";
    public const string RecurringGroup = "trykatch-module-schedules";
    public const string WorkId = "workId";
    public const string TargetReference = "targetReference";
}

/// <summary>Fixed bridge from Quartz to the authoritative persisted work ledger.</summary>
internal sealed class ModuleWorkBridgeJob(ModuleJobProcessor processor) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        string? workIdText = context.MergedJobDataMap.GetString(ModuleQuartzKeys.WorkId);
        if (Guid.TryParseExact(workIdText, "N", out Guid workId) && workId != Guid.Empty)
            await processor.ExecuteWorkAsync(workId, cancellationToken);
    }
}

/// <summary>Fixed control job; its data contains only a reference to an explicitly registered target.</summary>
internal sealed class ModuleRecurringControlJob(ModuleJobProcessor processor) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        string? reference = context.MergedJobDataMap.GetString(ModuleQuartzKeys.TargetReference);
        if (ModuleJobProcessor.TryParseTargetReference(reference, out string scheduleKey, out string targetKey))
            await processor.ReconcileTargetAsync(scheduleKey, targetKey, cancellationToken);
    }
}
