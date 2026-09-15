using System.Globalization;
using __ROOT_NAMESPACE__.Modules;
using __ROOT_NAMESPACE__.Modules.__MODULE__.Domain;
using __ROOT_NAMESPACE__.Modules.__MODULE__.IntegrationEvents;

namespace __ROOT_NAMESPACE__.Modules.__MODULE__.Application;

using static __ROOT_NAMESPACE__.Modules.__MODULE__.Application.__ENTITY__Operations;

public sealed class Create__ENTITY__CommandHandler(
    I__ENTITY__Store store, IOrganizationModuleData context, IModulePermissionAuthorizer authorizer,
    TimeProvider timeProvider, __ENTITY__ReadModel reader)
{
    public async Task<__ENTITY__OperationResult<__ENTITY__Dto>> HandleAsync(Save__ENTITY__Command command, CancellationToken cancellationToken)
    {
        if (!await authorizer.HasPermissionAsync("__MODULE_ID__.manage", cancellationToken)) return Forbidden<__ENTITY__Dto>();
        string? error = Validate(command);
        if (error is not null) return __ENTITY__Operation.Failure<__ENTITY__Dto>("validation", error);
        __ENTITY__Record record = __ENTITY__Record.Create(context.OrganizationId, context.ActorId,
            __COMMAND_TO_DOMAIN_ARGUMENTS__,
            timeProvider.GetUtcNow());
        store.Add(record);
        DateTimeOffset occurredAt = timeProvider.GetUtcNow();
        RecordChange(context, record, "created", new __ENTITY__Created(
            record.Id, record.OrganizationId, context.ActorId, occurredAt));
        await store.SaveChangesAsync(cancellationToken);
        return __ENTITY__Operation.Success(await reader.MapAsync(record, cancellationToken));
    }

}

public sealed class Update__ENTITY__CommandHandler(
    I__ENTITY__Store store, IOrganizationModuleData context, IModulePermissionAuthorizer authorizer,
    TimeProvider timeProvider, __ENTITY__ReadModel reader)
{
    public async Task<__ENTITY__OperationResult<__ENTITY__Dto>> HandleAsync(Guid id, Guid expectedVersion, Save__ENTITY__Command command, CancellationToken cancellationToken)
    {
        if (!await authorizer.HasPermissionAsync("__MODULE_ID__.manage", cancellationToken)) return Forbidden<__ENTITY__Dto>();
        string? error = Validate(command);
        if (error is not null) return __ENTITY__Operation.Failure<__ENTITY__Dto>("validation", error);
        __ENTITY__Record? record = await store.FindAsync(id, false, cancellationToken);
        if (record is null) return NotFound<__ENTITY__Dto>();
        record.EnsureVersion(expectedVersion);
        record.Update(
            __COMMAND_TO_DOMAIN_ARGUMENTS__,
            timeProvider.GetUtcNow());
        DateTimeOffset occurredAt = timeProvider.GetUtcNow();
        RecordChange(context, record, "updated", new __ENTITY__Updated(
            record.Id, record.OrganizationId, context.ActorId, occurredAt));
        await store.SaveChangesAsync(cancellationToken);
        return __ENTITY__Operation.Success(await reader.MapAsync(record, cancellationToken));
    }

}

public sealed class __ENTITY__RecoveryCommands(
    I__ENTITY__Store store, IOrganizationModuleData context, IModulePermissionAuthorizer authorizer, TimeProvider timeProvider)
{
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
        if (!await authorizer.HasPermissionAsync("__MODULE_ID__.manage", cancellationToken)) return Forbidden<bool>();
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
            RecordChange(context, record, "deletion-requested", new __ENTITY__DeletionRequested(
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
        if (!await authorizer.HasPermissionAsync("__MODULE_ID__.manage", cancellationToken)) return Forbidden<bool>();
        __ENTITY__Record? record = await store.FindAsync(id, includeRecoverable, cancellationToken);
        if (record is null) return NotFound<bool>();
        record.EnsureVersion(expectedVersion);
        if (change(record, context, timeProvider.GetUtcNow()))
        {
            DateTimeOffset occurredAt = timeProvider.GetUtcNow();
            RecordChange(context, record, operation, createIntegrationEvent(record, context.ActorId, occurredAt));
            await store.SaveChangesAsync(cancellationToken);
        }
        return __ENTITY__Operation.Success(true);
    }

}
