namespace __ROOT_NAMESPACE__.Modules.__MODULE__.Domain;

public enum __ENTITY__WorkflowState { __WORKFLOW_STATES__ }

public sealed class BlueprintRuleException(string code, string field, string message) : Exception(message)
{
    public string Code { get; } = code;
    public string Field { get; } = field;
}

public sealed class BlueprintConflictException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed partial class __ENTITY__Record
{
    public Guid Version { get; private set; } = Guid.NewGuid();
    public __ENTITY__WorkflowState WorkflowState { get; private set; } = __ENTITY__WorkflowState.__INITIAL_STATE__;
    public string? DecisionReason { get; private set; }
    public bool CanEdit => LifecycleState == __ENTITY__LifecycleState.Active && WorkflowState is __EDITABLE_STATES__;

    public void EnsureVersion(Guid expectedVersion)
    {
        if (expectedVersion == Guid.Empty || expectedVersion != Version)
            throw new BlueprintConflictException("stale_version", "This record changed. Refresh it before trying again.");
    }

    private void EnsureEditable()
    {
        if (!CanEdit) throw new BlueprintConflictException("not_editable", "This record cannot be edited in its current state.");
    }

    private static void ValidateBusinessFields(__DOMAIN_FIELD_PARAMETERS__)
    {
        __DOMAIN_CONSTRAINTS__
    }

    __DOMAIN_ACTIONS__
}
