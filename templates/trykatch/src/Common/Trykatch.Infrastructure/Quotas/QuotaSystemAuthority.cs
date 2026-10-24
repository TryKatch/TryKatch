using Trykatch.Infrastructure.Execution;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Quotas;

internal static class QuotaSystemAuthority
{
    // Static provenance only; the caller must separately confirm current organization and placement.
    internal static bool IsAllowed(ModuleWorkExecutionContext work, ModuleOperationRegistry operations,
        Guid organizationId, Guid actorId, string ownerModuleId, string resourceKey)
    {
        ModuleExecutionIdentity? identity = work.Identity;
        ModuleOperationDescriptor? operation = work.Operation;
        return identity is not null && operation is not null && identity.OrganizationId == organizationId
            && identity.EffectiveActorId == actorId && identity.OriginActorId is { } origin && origin != Guid.Empty
            && operation.ModuleId == ownerModuleId
            && operations.TryGet(ownerModuleId, operation.OperationKey, operation.OperationVersion, out ModuleOperationRegistration? installed)
            && installed is not null && ReferenceEquals(installed.Descriptor, operation)
            && SystemWorkGrantValidator.TryValidate(installed, identity)
            && installed.SystemGrant!.AllowedQuotaResources.Contains(resourceKey, StringComparer.Ordinal);
    }
}
