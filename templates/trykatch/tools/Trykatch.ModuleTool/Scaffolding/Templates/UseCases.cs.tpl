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
    DateTimeOffset? UpdatedAt, __ENTITY__LifecycleDto Lifecycle);
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

public sealed class __MODULE__UseCases(
    I__ENTITY__Store store,
    IOrganizationModuleData context,
    IModulePermissionAuthorizer authorizer,
    TimeProvider timeProvider)
{
    public async Task<__ENTITY__OperationResult<__ENTITY__Dto[]>> ListAsync(string lifecycle, CancellationToken cancellationToken)
    {
        if (!await authorizer.HasPermissionAsync("__MODULE_ID__.read", cancellationToken))
            return __ENTITY__Operation.Failure<__ENTITY__Dto[]>("forbidden", "Records cannot be viewed by this membership.");
        __ENTITY__QueryScope scope = lifecycle.ToLowerInvariant() switch
        {
            "active" => __ENTITY__QueryScope.Active,
            "recoverable" => __ENTITY__QueryScope.Recoverable,
            _ => throw new ArgumentException("Lifecycle must be active or recoverable.", nameof(lifecycle))
        };
        IReadOnlyList<__ENTITY__Record> records = await store.ListAsync(scope, cancellationToken);
        return __ENTITY__Operation.Success(records.Select(ToDto).ToArray());
    }

    public async Task<__ENTITY__OperationResult<__ENTITY__Dto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await authorizer.HasPermissionAsync("__MODULE_ID__.read", cancellationToken)) return Forbidden<__ENTITY__Dto>();
        __ENTITY__Record? record = await store.FindAsync(id, false, cancellationToken);
        return record is null ? NotFound<__ENTITY__Dto>() : __ENTITY__Operation.Success(ToDto(record));
    }

    public async Task<__ENTITY__OperationResult<__ENTITY__Dto>> CreateAsync(Save__ENTITY__Command command, CancellationToken cancellationToken)
    {
        if (!await CanManage(cancellationToken)) return Forbidden<__ENTITY__Dto>();
        string? error = Validate(command);
        if (error is not null) return __ENTITY__Operation.Failure<__ENTITY__Dto>("validation", error);
        __ENTITY__Record record = __ENTITY__Record.Create(context.OrganizationId, context.ActorId,
            __COMMAND_TO_DOMAIN_ARGUMENTS__,
            timeProvider.GetUtcNow());
        store.Add(record);
        RecordChange(record, "created");
        await store.SaveChangesAsync(cancellationToken);
        return __ENTITY__Operation.Success(ToDto(record));
    }

    public async Task<__ENTITY__OperationResult<__ENTITY__Dto>> UpdateAsync(Guid id, Save__ENTITY__Command command, CancellationToken cancellationToken)
    {
        if (!await CanManage(cancellationToken)) return Forbidden<__ENTITY__Dto>();
        string? error = Validate(command);
        if (error is not null) return __ENTITY__Operation.Failure<__ENTITY__Dto>("validation", error);
        __ENTITY__Record? record = await store.FindAsync(id, false, cancellationToken);
        if (record is null) return NotFound<__ENTITY__Dto>();
        record.Update(
            __COMMAND_TO_DOMAIN_ARGUMENTS__,
            timeProvider.GetUtcNow());
        RecordChange(record, "updated");
        await store.SaveChangesAsync(cancellationToken);
        return __ENTITY__Operation.Success(ToDto(record));
    }

    public Task<__ENTITY__OperationResult<bool>> ArchiveAsync(Guid id, CancellationToken cancellationToken) =>
        ChangeLifecycleAsync(id, "archived", static (record, context, now) => record.Archive(context.ActorId, now), false, cancellationToken);

    public Task<__ENTITY__OperationResult<bool>> RestoreAsync(Guid id, CancellationToken cancellationToken) =>
        ChangeLifecycleAsync(id, "restored", static (record, _, _) => record.Restore(), true, cancellationToken);

    public async Task<__ENTITY__OperationResult<bool>> RequestDeletionAsync(Guid id, string? requestedReason, CancellationToken cancellationToken)
    {
        if (!await CanManage(cancellationToken)) return Forbidden<bool>();
        string reason = requestedReason?.Trim() ?? string.Empty;
        if (reason.Length is < 10 or > 500)
            return __ENTITY__Operation.Failure<bool>("validation", "A deletion reason containing 10-500 characters is required.");
        __ENTITY__Record? record = await store.FindAsync(id, true, cancellationToken);
        if (record is null) return NotFound<bool>();
        if (record.LifecycleState != __ENTITY__LifecycleState.Archived)
            return __ENTITY__Operation.Failure<bool>("conflict", "Archive the record before requesting deletion.");
        if (record.RequestDeletion(context.ActorId, reason, timeProvider.GetUtcNow()))
        {
            RecordChange(record, "deleted");
            await store.SaveChangesAsync(cancellationToken);
        }
        return __ENTITY__Operation.Success(true);
    }

    private async Task<__ENTITY__OperationResult<bool>> ChangeLifecycleAsync(Guid id, string operation,
        Func<__ENTITY__Record, IOrganizationModuleData, DateTimeOffset, bool> change, bool includeRecoverable,
        CancellationToken cancellationToken)
    {
        if (!await CanManage(cancellationToken)) return Forbidden<bool>();
        __ENTITY__Record? record = await store.FindAsync(id, includeRecoverable, cancellationToken);
        if (record is null) return NotFound<bool>();
        if (change(record, context, timeProvider.GetUtcNow()))
        {
            RecordChange(record, operation);
            await store.SaveChangesAsync(cancellationToken);
        }
        return __ENTITY__Operation.Success(true);
    }

    private Task<bool> CanManage(CancellationToken cancellationToken) => authorizer.HasPermissionAsync("__MODULE_ID__.manage", cancellationToken);

    private void RecordChange(__ENTITY__Record record, string operation)
    {
        context.RecordAudit("__MODULE_ID__." + operation, "__ENTITY__", record.Id.ToString(), __AUDIT_DISPLAY__);
        context.Enqueue(new __ENTITY__Changed(record.Id, record.OrganizationId, operation, context.ActorId,
            timeProvider.GetUtcNow(), record.DeletionReason));
    }

    private static string? Validate(Save__ENTITY__Command command)
    {
        List<string> errors = [];
        __FIELD_VALIDATION__
        return errors.Count == 0 ? null : string.Join(" ", errors);
    }

    private static __ENTITY__OperationResult<T> Forbidden<T>() =>
        __ENTITY__Operation.Failure<T>("forbidden", "Records cannot be changed by this membership.");
    private static __ENTITY__OperationResult<T> NotFound<T>() =>
        __ENTITY__Operation.Failure<T>("not_found", "Record was not found.");

    private static __ENTITY__Dto ToDto(__ENTITY__Record record) => new(
        record.Id,
        __DTO_ARGUMENTS__,
        record.CreatedAt, record.UpdatedAt,
        new(record.LifecycleState.ToString(), record.ArchivedAt, record.ArchivedBy,
            record.DeletedAt, record.DeletedBy, record.DeletionReason));
}
