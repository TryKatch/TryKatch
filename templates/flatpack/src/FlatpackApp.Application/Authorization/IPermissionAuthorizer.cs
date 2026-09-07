namespace FlatpackApp.Application.Authorization;

public interface IPermissionAuthorizer
{
    Task<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken = default);
}

