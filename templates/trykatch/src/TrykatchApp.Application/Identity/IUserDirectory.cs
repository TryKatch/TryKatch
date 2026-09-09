namespace TrykatchApp.Application.Identity;

public sealed record UserSummary(Guid Id, string Email, string DisplayName);

public interface IUserDirectory
{
    Task<bool> UserExistsAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<Guid?> FindUserIdByEmailAsync(string email, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<Guid, UserSummary>> GetUsersAsync(IEnumerable<Guid> userIds, CancellationToken cancellationToken = default);
}
