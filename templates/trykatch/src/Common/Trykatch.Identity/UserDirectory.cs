using Trykatch.Application.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Trykatch.Identity;

internal sealed class UserDirectory(UserManager<ApplicationUser> users) : IUserDirectory
{
    public Task<bool> UserExistsAsync(Guid userId, CancellationToken cancellationToken = default) =>
        users.Users.AnyAsync(x => x.Id == userId, cancellationToken);

    public Task<Guid?> FindUserIdByEmailAsync(string email, CancellationToken cancellationToken = default) =>
        users.Users.Where(x => x.NormalizedEmail == users.NormalizeEmail(email)).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, UserSummary>> GetUsersAsync(IEnumerable<Guid> userIds, CancellationToken cancellationToken = default)
    {
        Guid[] ids = userIds.Distinct().ToArray();
        return await users.Users
            .Where(x => ids.Contains(x.Id))
            .Select(x => new UserSummary(x.Id, x.Email ?? string.Empty, x.DisplayName))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
    }
}
