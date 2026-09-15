using System.Globalization;
using __ROOT_NAMESPACE__.Modules;
using __ROOT_NAMESPACE__.Modules.__MODULE__.Domain;
using __ROOT_NAMESPACE__.Modules.__MODULE__.IntegrationEvents;

namespace __ROOT_NAMESPACE__.Modules.__MODULE__.Application;

using static __ROOT_NAMESPACE__.Modules.__MODULE__.Application.__ENTITY__Operations;

public sealed class List__ENTITY__QueryHandler(
    I__ENTITY__Store store, IModulePermissionAuthorizer authorizer, __ENTITY__ReadModel reader)
{
    public async Task<__ENTITY__OperationResult<__ENTITY__Dto[]>> HandleAsync(string lifecycle, CancellationToken cancellationToken)
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
        List<__ENTITY__Dto> results = [];
        foreach (__ENTITY__Record record in records)
            results.Add(await reader.MapAsync(record, cancellationToken));
        return __ENTITY__Operation.Success(results.ToArray());
    }

}

public sealed class Get__ENTITY__QueryHandler(
    I__ENTITY__Store store, IModulePermissionAuthorizer authorizer, __ENTITY__ReadModel reader)
{
    public async Task<__ENTITY__OperationResult<__ENTITY__Dto>> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await authorizer.HasPermissionAsync("__MODULE_ID__.read", cancellationToken)) return Forbidden<__ENTITY__Dto>();
        __ENTITY__Record? record = await store.FindAsync(id, false, cancellationToken);
        return record is null ? NotFound<__ENTITY__Dto>() : __ENTITY__Operation.Success(await reader.MapAsync(record, cancellationToken));
    }

}
