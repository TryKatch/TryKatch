using FlatpackApp.Application.Authorization;

namespace FlatpackApp.Infrastructure.Organizations;

internal sealed class PermissionAuthorizer(OrganizationContext context, IPermissionCatalog permissionCatalog) : IPermissionAuthorizer
{
    public Task<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken = default) =>
        Task.FromResult(permissionCatalog.Contains(permission) && context.IsResolved && context.Permissions.Contains(permission));
}
