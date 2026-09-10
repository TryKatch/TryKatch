using Trykatch.Application.Authorization;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Organizations;

internal sealed class PermissionAuthorizer(OrganizationContext context, IPermissionCatalog permissionCatalog) :
    IPermissionAuthorizer,
    IModulePermissionAuthorizer
{
    public Task<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken = default) =>
        Task.FromResult(permissionCatalog.Contains(permission) && context.IsResolved && context.Permissions.Contains(permission));
}
