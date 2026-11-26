using Trykatch.Infrastructure.Execution;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Quotas;

internal static class QuotaSystemAuthority
{
    // Static provenance only; the caller must separately confirm current organization and placement.
    internal static bool IsAllowed(ModuleWorkExecutionContext work, ModuleCatalog catalog,
        Guid organizationId, Guid actorId, string ownerModuleId, string resourceKey)
    {
        ModuleExecutionIdentity? identity = work.Identity;
        ModuleOperationDescriptor? operation = work.Operation;
        ModuleDescriptor? module = catalog.Descriptors.SingleOrDefault(module => module.Id == ownerModuleId);
        ModuleOperationDescriptor? installed = operation is null ? null : module?.Operations.SingleOrDefault(candidate =>
            candidate.OperationKey == operation.OperationKey && candidate.OperationVersion == operation.OperationVersion);
        ModuleSystemGrantDescriptor? grant = module?.SystemGrants.SingleOrDefault(candidate =>
            candidate.GrantKey == installed?.SystemGrantKey);
        return identity is not null && operation is not null && identity.OrganizationId == organizationId
            && identity.EffectiveActorId == actorId && identity.OriginActorId is { } origin && origin != Guid.Empty
            && operation.ModuleId == ownerModuleId
            && installed is not null && ReferenceEquals(installed, operation)
            && SystemWorkGrantValidator.TryValidate(installed, grant, identity)
            && grant!.AllowedQuotaResources.Contains(resourceKey, StringComparer.Ordinal);
    }
}
