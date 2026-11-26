using Trykatch.Modules;

namespace Trykatch.Infrastructure.Execution;

/// <summary>Scope-local provenance established only by the host after authorization.</summary>
internal sealed class ModuleWorkExecutionContext
{
    internal ModuleExecutionIdentity? Identity { get; private set; }
    internal ModuleOperationDescriptor? Operation { get; private set; }

    internal void Initialize(ModuleExecutionIdentity identity, ModuleOperationDescriptor operation)
    {
        if (Identity is not null) throw new InvalidOperationException("Work provenance is already initialized.");
        Identity = identity;
        Operation = operation;
    }
}
