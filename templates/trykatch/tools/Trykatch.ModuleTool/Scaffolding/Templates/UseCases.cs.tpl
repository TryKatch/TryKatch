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
    DateTimeOffset? UpdatedAt, __ENTITY__LifecycleDto Lifecycle, Guid Version);
public sealed record __ENTITY__OperationResult<T>(bool IsSuccess, T? Value, string? Code, string? Error);

public static class __ENTITY__Operation
{
    public static __ENTITY__OperationResult<T> Success<T>(T value) => new(true, value, null, null);
    public static __ENTITY__OperationResult<T> Failure<T>(string code, string error) => new(false, default, code, error);
}

public enum __ENTITY__QueryScope { Active, Recoverable }

public interface I__ENTITY__Store
{
    Task<__ENTITY__Page<__ENTITY__Record>> PageAsync(__ENTITY__PageQuery query, CancellationToken cancellationToken);
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
    public async Task<__ENTITY__OperationResult<__ENTITY__Page<__ENTITY__Dto>>> PageAsync(__ENTITY__PageQuery query, CancellationToken cancellationToken)
    {
        if (!await authorizer.HasPermissionAsync("__MODULE_ID__.read", cancellationToken)) return Forbidden<__ENTITY__Page<__ENTITY__Dto>>();
        query.Validate();
        __ENTITY__Page<__ENTITY__Record> page = await store.PageAsync(query, cancellationToken);
        return __ENTITY__Operation.Success(new __ENTITY__Page<__ENTITY__Dto>(page.Items.Select(ToDto).ToArray(), page.Page, page.PageSize, page.HasMore));
    }

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
        DateTimeOffset occurredAt = timeProvider.GetUtcNow();
        RecordChange(record, "created", new __ENTITY__Created(
            record.Id, record.OrganizationId, context.ActorId, occurredAt));
        await store.SaveChangesAsync(cancellationToken);
        return __ENTITY__Operation.Success(ToDto(record));
    }

    public async Task<__ENTITY__OperationResult<__ENTITY__Dto>> UpdateAsync(Guid id, Guid expectedVersion, Save__ENTITY__Command command, CancellationToken cancellationToken)
    {
        if (!await CanManage(cancellationToken)) return Forbidden<__ENTITY__Dto>();
        string? error = Validate(command);
        if (error is not null) return __ENTITY__Operation.Failure<__ENTITY__Dto>("validation", error);
        __ENTITY__Record? record = await store.FindAsync(id, false, cancellationToken);
        if (record is null) return NotFound<__ENTITY__Dto>();
        record.EnsureVersion(expectedVersion);
        record.Update(
            __COMMAND_TO_DOMAIN_ARGUMENTS__,
            timeProvider.GetUtcNow());
        DateTimeOffset occurredAt = timeProvider.GetUtcNow();
        RecordChange(record, "updated", new __ENTITY__Updated(
            record.Id, record.OrganizationId, context.ActorId, occurredAt));
        await store.SaveChangesAsync(cancellationToken);
        return __ENTITY__Operation.Success(ToDto(record));
    }

    public Task<__ENTITY__OperationResult<bool>> ArchiveAsync(Guid id, Guid expectedVersion, CancellationToken cancellationToken) =>
        ChangeLifecycleAsync(id, expectedVersion, "archived", static (record, context, now) => record.Archive(context.ActorId, now),
            static (record, actorId, occurredAt) => new __ENTITY__Archived(record.Id, record.OrganizationId, actorId, occurredAt),
            false, cancellationToken);

    public Task<__ENTITY__OperationResult<bool>> RestoreAsync(Guid id, Guid expectedVersion, CancellationToken cancellationToken) =>
        ChangeLifecycleAsync(id, expectedVersion, "restored", static (record, _, _) => record.Restore(),
            static (record, actorId, occurredAt) => new __ENTITY__Restored(record.Id, record.OrganizationId, actorId, occurredAt),
            true, cancellationToken);

    public async Task<__ENTITY__OperationResult<bool>> RequestDeletionAsync(Guid id, Guid expectedVersion, string? requestedReason, CancellationToken cancellationToken)
    {
        if (!await CanManage(cancellationToken)) return Forbidden<bool>();
        string reason = requestedReason?.Trim() ?? string.Empty;
        if (reason.Length is < 10 or > 500)
            return __ENTITY__Operation.Failure<bool>("validation", "A deletion reason containing 10-500 characters is required.");
        __ENTITY__Record? record = await store.FindAsync(id, true, cancellationToken);
        if (record is null) return NotFound<bool>();
        record.EnsureVersion(expectedVersion);
        if (record.LifecycleState != __ENTITY__LifecycleState.Archived)
            return __ENTITY__Operation.Failure<bool>("conflict", "Archive the record before requesting deletion.");
        if (record.RequestDeletion(context.ActorId, reason, timeProvider.GetUtcNow()))
        {
            DateTimeOffset occurredAt = timeProvider.GetUtcNow();
            RecordChange(record, "deletion-requested", new __ENTITY__DeletionRequested(
                record.Id, record.OrganizationId, context.ActorId, occurredAt, reason));
            await store.SaveChangesAsync(cancellationToken);
        }
        return __ENTITY__Operation.Success(true);
    }

    private async Task<__ENTITY__OperationResult<bool>> ChangeLifecycleAsync<TIntegrationEvent>(Guid id, Guid expectedVersion, string operation,
        Func<__ENTITY__Record, IOrganizationModuleData, DateTimeOffset, bool> change,
        Func<__ENTITY__Record, Guid, DateTimeOffset, TIntegrationEvent> createIntegrationEvent, bool includeRecoverable,
        CancellationToken cancellationToken)
        where TIntegrationEvent : notnull
    {
        if (!await CanManage(cancellationToken)) return Forbidden<bool>();
        __ENTITY__Record? record = await store.FindAsync(id, includeRecoverable, cancellationToken);
        if (record is null) return NotFound<bool>();
        record.EnsureVersion(expectedVersion);
        if (change(record, context, timeProvider.GetUtcNow()))
        {
            DateTimeOffset occurredAt = timeProvider.GetUtcNow();
            RecordChange(record, operation, createIntegrationEvent(record, context.ActorId, occurredAt));
            await store.SaveChangesAsync(cancellationToken);
        }
        return __ENTITY__Operation.Success(true);
    }

    private Task<bool> CanManage(CancellationToken cancellationToken) => authorizer.HasPermissionAsync("__MODULE_ID__.manage", cancellationToken);

    private void RecordChange<TIntegrationEvent>(
        __ENTITY__Record record,
        string operation,
        TIntegrationEvent integrationEvent)
        where TIntegrationEvent : notnull
    {
        context.RecordAudit("__MODULE_ID__." + operation, "__ENTITY__", record.Id.ToString(), __AUDIT_DISPLAY__);
        context.Enqueue(integrationEvent);
    }

    private static string NormalizeAuditDisplay(string? value, Guid id)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0) return id.ToString();
        return normalized.Length <= 240 ? normalized : normalized[..240];
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
            record.DeletedAt, record.DeletedBy, record.DeletionReason), record.Version);
}
