using Trykatch.Application.Common;

namespace Trykatch.Application.Jobs;

public sealed record ModuleWorkView(Guid WorkId, Guid OrganizationId, string JobKey, int JobVersion,
    string State, int Attempts, bool OperatorPaused, int RecoveryGeneration, DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DueAtUtc, DateTimeOffset? NextAttemptAtUtc, string? FailureCode);

public sealed record ModuleWorkActionRequest(Guid RequestId, int ExpectedGeneration);
public sealed record ModuleWorkActionReceipt(Guid WorkId, string Action, int RecoveryGeneration, bool OperatorPaused);

public interface IModuleWorkAdministration
{
    Task<Result<PagedResult<ModuleWorkView>>> ListAsync(Guid actorId, int page, int pageSize, string? state,
        CancellationToken cancellationToken = default);
    Task<Result<ModuleWorkActionReceipt>> ChangeAsync(Guid actorId, Guid workId, string action,
        ModuleWorkActionRequest request, CancellationToken cancellationToken = default);
}
