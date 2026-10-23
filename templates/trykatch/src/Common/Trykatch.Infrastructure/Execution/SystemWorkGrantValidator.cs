using Trykatch.Modules;

namespace Trykatch.Infrastructure.Execution;

/// <summary>Checks persisted system provenance against one finite installed grant.</summary>
internal static class SystemWorkGrantValidator
{
    public static bool TryValidate(ModuleOperationRegistration registration, ModuleExecutionIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(identity);

        ModuleOperationDescriptor operation = registration.Descriptor;
        ModuleSystemGrantDescriptor? grant = registration.SystemGrant;
        if (grant is null
            || identity.CatalogVersion != ModuleOperationRegistry.CatalogVersion
            || identity.OrganizationId == Guid.Empty
            || identity.EffectiveActorId == Guid.Empty
            || identity.OriginActorId == Guid.Empty
            || identity.MembershipId is not null
            || operation.ExecutionKind is not (ModuleExecutionKind.CommittedEvent or ModuleExecutionKind.ScheduledSystem)
            || identity.ExecutionKind != operation.ExecutionKind
            || identity.EffectiveActorId != grant.AuditActorId
            || operation.ModuleId != grant.OwnerModuleId
            || operation.SystemGrantKey != grant.GrantKey
            || identity.GrantKey != grant.GrantKey
            || identity.GrantVersion != grant.GrantVersion
            || !grant.AllowedOperationKeys.Contains(operation.OperationKey, StringComparer.Ordinal)
            || !grant.AllowedResourceKeys.Contains(operation.ResourcePolicyKey, StringComparer.Ordinal))
            return false;

        return operation.ExecutionKind switch
        {
            ModuleExecutionKind.CommittedEvent => identity.SourceMessageId is { } sourceMessageId && sourceMessageId != Guid.Empty,
            ModuleExecutionKind.ScheduledSystem => identity.SourceMessageId is null,
            _ => false
        };
    }
}
