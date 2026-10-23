namespace Trykatch.Application.Execution;

/// <summary>A host-only reference to persisted work; callers cannot supply authority or payload.</summary>
public sealed record ModuleExecutionRequest(Guid WorkId, string ExpectedOperationKey, int ExpectedOperationVersion);

public enum ModuleExecutionState
{
    Completed,
    AlreadyCompleted,
    Paused,
    Denied,
    RetryableFailure,
    TerminalFailure
}

/// <summary>A value-free execution result. Cancellation can propagate before acknowledgement.</summary>
public sealed record ModuleExecutionOutcome(ModuleExecutionState State, string? FailureCode = null, DateTimeOffset? RetryAtUtc = null);

/// <summary>Host implementation follows in E2; this contract does not establish an organization scope.</summary>
public interface IOrganizationWorkExecutor
{
    Task<ModuleExecutionOutcome> ExecuteAsync(ModuleExecutionRequest request, CancellationToken cancellationToken);
}
