using Trykatch.Infrastructure.Execution;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Jobs;

internal sealed record ModuleJobRegistration(
    ModuleJobDescriptor Descriptor,
    ModuleOperationRegistration Operation);

/// <summary>Resolves declared jobs through the installed module catalog and operation registry.</summary>
internal sealed class ModuleJobRegistry
{
    private readonly Dictionary<(string JobKey, int Version), ModuleJobRegistration> registrations;

    public ModuleJobRegistry(ModuleCatalog catalog, ModuleOperationRegistry operations)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(operations);

        Dictionary<(string JobKey, int Version), ModuleJobRegistration> result = [];
        foreach (ModuleJobDescriptor job in catalog.Descriptors.SelectMany(module => module.Jobs))
        {
            if (job.ExecutionKind is not (ModuleExecutionKind.UserCommand or ModuleExecutionKind.ScheduledSystem)
                || !operations.TryGet(job.ModuleId, job.OperationKey, job.OperationVersion, out ModuleOperationRegistration? operation)
                || operation is null
                || operation.Descriptor.ExecutionKind != job.ExecutionKind
                || operation.Descriptor.ModuleId != job.ModuleId
                || operation.Descriptor.PayloadSchemaKey != job.PayloadSchemaKey
                || operation.Descriptor.PayloadSchemaVersion != job.PayloadSchemaVersion
                || job.MaximumPayloadBytes > operation.Descriptor.MaximumPayloadBytes)
                throw new InvalidOperationException(
                    $"Job '{job.ModuleId}/{job.JobKey}' does not match an installed operation.");

            if (!result.TryAdd((job.JobKey, job.JobVersion), new ModuleJobRegistration(job, operation)))
                throw new InvalidOperationException($"Duplicate installed job '{job.JobKey}' version {job.JobVersion}.");
        }

        registrations = result;
    }

    public bool TryGet(string jobKey, int jobVersion, out ModuleJobRegistration? registration) =>
        registrations.TryGetValue((jobKey, jobVersion), out registration);
}
