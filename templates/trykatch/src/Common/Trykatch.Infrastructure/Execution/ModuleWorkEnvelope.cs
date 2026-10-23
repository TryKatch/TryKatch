using Trykatch.Modules;

namespace Trykatch.Infrastructure.Execution;

internal sealed record ModuleWorkEnvelope(
    int Version,
    string ModuleId,
    ModuleExecutionIdentity Identity);
