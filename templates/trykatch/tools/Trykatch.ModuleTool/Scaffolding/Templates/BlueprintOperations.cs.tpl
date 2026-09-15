using System.Globalization;
using __ROOT_NAMESPACE__.Modules;
using __ROOT_NAMESPACE__.Modules.__MODULE__.Domain;
using __ROOT_NAMESPACE__.Modules.__MODULE__.IntegrationEvents;

namespace __ROOT_NAMESPACE__.Modules.__MODULE__.Application;

internal static class __ENTITY__Operations
{
    internal static void RecordChange<TIntegrationEvent>(
        IOrganizationModuleData context,
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

    internal static string? Validate(Save__ENTITY__Command command)
    {
        List<string> errors = [];
        __FIELD_VALIDATION__
        return errors.Count == 0 ? null : string.Join(" ", errors);
    }

    internal static __ENTITY__OperationResult<T> Forbidden<T>() =>
        __ENTITY__Operation.Failure<T>("forbidden", "Records cannot be changed by this membership.");
    internal static __ENTITY__OperationResult<T> NotFound<T>() =>
        __ENTITY__Operation.Failure<T>("not_found", "Record was not found.");

}
