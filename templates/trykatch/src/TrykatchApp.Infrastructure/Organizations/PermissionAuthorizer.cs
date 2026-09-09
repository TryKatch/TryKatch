using TrykatchApp.Application.Authorization;

namespace TrykatchApp.Infrastructure.Organizations;

internal sealed class PermissionAuthorizer(OrganizationContext context, IPermissionCatalog permissionCatalog) : IPermissionAuthorizer
{
    public Task<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken = default) =>
        Task.FromResult(permissionCatalog.Contains(permission) && context.IsResolved && context.Permissions.Contains(permission));
}
