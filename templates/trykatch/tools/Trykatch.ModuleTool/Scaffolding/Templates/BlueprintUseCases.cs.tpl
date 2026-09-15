using System.Globalization;
using __ROOT_NAMESPACE__.Modules;
using __ROOT_NAMESPACE__.Modules.__MODULE__.Domain;
using __ROOT_NAMESPACE__.Modules.__MODULE__.IntegrationEvents;

namespace __ROOT_NAMESPACE__.Modules.__MODULE__.Application;

public sealed record Save__ENTITY__Command(
    __COMMAND_FIELDS__);
public sealed record __ENTITY__LifecycleDto(string Status, DateTimeOffset? ArchivedAt, Guid? ArchivedBy,
    DateTimeOffset? DeletedAt, Guid? DeletedBy, string? DeletionReason);
public sealed record __ENTITY__Dto(Guid Id,
    __DTO_FIELDS__,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt, __ENTITY__LifecycleDto Lifecycle, Guid Version, string WorkflowState, string? DecisionReason, bool CanEdit, string[] AvailableActions);
public sealed record __ENTITY__OperationResult<T>(bool IsSuccess, T? Value, string? Code, string? Error);

public static class __ENTITY__Operation
{
    public static __ENTITY__OperationResult<T> Success<T>(T value) => new(true, value, null, null);
    public static __ENTITY__OperationResult<T> Failure<T>(string code, string error) => new(false, default, code, error);
}

public enum __ENTITY__QueryScope { Active, Recoverable }

public interface I__ENTITY__Store
{
    Task<IReadOnlyList<__ENTITY__Record>> ListAsync(__ENTITY__QueryScope scope, CancellationToken cancellationToken);
    Task<__ENTITY__Record?> FindAsync(Guid id, bool includeRecoverable, CancellationToken cancellationToken);
    void Add(__ENTITY__Record record);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
