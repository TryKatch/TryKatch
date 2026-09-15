using System.Globalization;
using __ROOT_NAMESPACE__.Modules;
using __ROOT_NAMESPACE__.Modules.__MODULE__.Domain;
using __ROOT_NAMESPACE__.Modules.__MODULE__.IntegrationEvents;

namespace __ROOT_NAMESPACE__.Modules.__MODULE__.Application;

public sealed class __ENTITY__ReadModel(IModulePermissionAuthorizer authorizer)
{
    public async Task<__ENTITY__Dto> MapAsync(__ENTITY__Record record, CancellationToken cancellationToken) =>
        ToDto(record, await AvailableActionsAsync(record, cancellationToken));

    private async Task<string[]> AvailableActionsAsync(__ENTITY__Record record, CancellationToken cancellationToken)
    {
        List<string> actions = [];
        __AVAILABLE_ACTIONS__
        return actions.ToArray();
    }

    internal static __ENTITY__Dto ToDto(__ENTITY__Record record, string[] availableActions) => new(
        record.Id,
        __DTO_ARGUMENTS__,
        record.CreatedAt, record.UpdatedAt,
        new(record.LifecycleState.ToString(), record.ArchivedAt, record.ArchivedBy,
            record.DeletedAt, record.DeletedBy, record.DeletionReason), record.Version, record.WorkflowState.ToString(), record.DecisionReason, record.CanEdit, availableActions);
}
