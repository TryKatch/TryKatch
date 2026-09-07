using System.Text;
using System.Data;
using System.Collections.Frozen;
using FlatpackApp.Application.Common;
using FlatpackApp.Application.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FlatpackApp.Identity;

internal sealed class PlatformAccessDirectory(
    IdentityDbContext database,
    UserManager<ApplicationUser> users,
    RoleManager<IdentityRole<Guid>> roles) : IPlatformAccessDirectory
{
    private const string CustomRolePrefix = "platform-custom-";
    private const string DisplayNameClaim = "platform_role_name";
    private const string DescriptionClaim = "platform_role_description";
    private const string PermissionClaim = "platform_permission";

    public async Task<IReadOnlyList<PlatformRoleDefinition>> ListRolesAsync(CancellationToken cancellationToken = default)
    {
        List<PlatformRoleDefinition> definitions = [.. PlatformRoles.All];
        IdentityRole<Guid>[] customRoles = await roles.Roles.AsNoTracking()
            .Where(role => role.Name != null && role.Name.StartsWith(CustomRolePrefix))
            .OrderBy(role => role.Name)
            .ToArrayAsync(cancellationToken);
        foreach (IdentityRole<Guid> role in customRoles)
        {
            definitions.Add(await ToCustomDefinitionAsync(role));
        }

        return definitions.OrderBy(role => role.Order).ThenBy(role => role.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<PlatformRoleDefinition?> FindRoleAsync(string roleKey, CancellationToken cancellationToken = default)
    {
        PlatformRoleDefinition? builtIn = PlatformRoles.Find(roleKey);
        if (builtIn is not null) return builtIn;
        if (!IsCustomRole(roleKey)) return null;
        IdentityRole<Guid>? role = await roles.Roles.SingleOrDefaultAsync(item => item.Name == roleKey, cancellationToken);
        return role is null ? null : await ToCustomDefinitionAsync(role);
    }

    public async Task<Result<PlatformRoleDefinition>> CreateRoleAsync(SavePlatformRoleCommand command, CancellationToken cancellationToken = default)
    {
        Result<NormalizedRoleInput> normalized = await ValidateRoleAsync(command, null, cancellationToken);
        if (!normalized.IsSuccess) return Result.Failure<PlatformRoleDefinition>(normalized.ErrorCode!, normalized.ErrorMessage!);

        return await database.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            IdentityRole<Guid> role = new($"{CustomRolePrefix}{Guid.CreateVersion7():N}") { Id = Guid.CreateVersion7() };
            await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            IdentityResult created = await roles.CreateAsync(role);
            if (!created.Succeeded) return IdentityFailure<PlatformRoleDefinition>(created);
            Result<bool> claimsResult = await ReplaceCustomRoleClaimsAsync(role, normalized.Value!, cancellationToken);
            if (!claimsResult.IsSuccess) return Result.Failure<PlatformRoleDefinition>(claimsResult.ErrorCode!, claimsResult.ErrorMessage!);
            await transaction.CommitAsync(cancellationToken);
            return Result.Success(await ToCustomDefinitionAsync(role));
        });
    }

    public async Task<Result<PlatformRoleDefinition>> UpdateRoleAsync(string roleKey, SavePlatformRoleCommand command, CancellationToken cancellationToken = default)
    {
        if (!IsCustomRole(roleKey)) return Result.Failure<PlatformRoleDefinition>("system_role", "Built-in platform roles cannot be changed.");
        IdentityRole<Guid>? role = await roles.FindByNameAsync(roleKey);
        if (role is null) return Result.Failure<PlatformRoleDefinition>("not_found", "Platform role was not found.");
        Result<NormalizedRoleInput> normalized = await ValidateRoleAsync(command, roleKey, cancellationToken);
        if (!normalized.IsSuccess) return Result.Failure<PlatformRoleDefinition>(normalized.ErrorCode!, normalized.ErrorMessage!);

        return await database.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            Result<bool> claimsResult = await ReplaceCustomRoleClaimsAsync(role, normalized.Value!, cancellationToken);
            if (!claimsResult.IsSuccess) return Result.Failure<PlatformRoleDefinition>(claimsResult.ErrorCode!, claimsResult.ErrorMessage!);
            await InvalidateRoleMembersAsync(role.Id, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Result.Success(await ToCustomDefinitionAsync(role));
        });
    }

    public async Task<Result<bool>> DeleteRoleAsync(string roleKey, CancellationToken cancellationToken = default)
    {
        if (!IsCustomRole(roleKey)) return Result.Failure<bool>("system_role", "Built-in platform roles cannot be deleted.");
        IdentityRole<Guid>? role = await roles.FindByNameAsync(roleKey);
        if (role is null) return Result.Failure<bool>("not_found", "Platform role was not found.");
        if (await database.UserRoles.AnyAsync(item => item.RoleId == role.Id, cancellationToken))
            return Result.Failure<bool>("role_in_use", "Move people to another role before deleting this role.");
        IdentityResult deleted = await roles.DeleteAsync(role);
        return deleted.Succeeded ? Result.Success(true) : IdentityFailure<bool>(deleted);
    }

    public async Task<PagedResult<PlatformAccessUser>> ListAsync(int page, int pageSize, string? search, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PlatformRoleDefinition> roleDefinitions = await ListRolesAsync(cancellationToken);
        Dictionary<string, PlatformRoleDefinition> roleMap = roleDefinitions.ToDictionary(role => role.Key, StringComparer.Ordinal);
        IQueryable<PlatformAccessRow> query = Query(roleMap.Keys.ToArray());
        if (!string.IsNullOrWhiteSpace(search))
        {
            string pattern = $"%{search.Trim()}%";
            query = query.Where(row =>
                EF.Functions.ILike(row.Email, pattern)
                || EF.Functions.ILike(row.DisplayName, pattern)
                || EF.Functions.ILike(row.RoleKey, pattern));
        }

        long totalCount = await query.LongCountAsync(cancellationToken);
        PlatformAccessRow[] rows = await query
            .OrderBy(row => row.DisplayName)
            .ThenBy(row => row.Email)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArrayAsync(cancellationToken);

        return new PagedResult<PlatformAccessUser>(rows.Select(row => ToUser(row, roleMap)).ToArray(), page, pageSize, totalCount);
    }

    public async Task<Result<PlatformAccessUser>> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PlatformRoleDefinition> roleDefinitions = await ListRolesAsync(cancellationToken);
        Dictionary<string, PlatformRoleDefinition> roleMap = roleDefinitions.ToDictionary(role => role.Key, StringComparer.Ordinal);
        PlatformAccessRow? row = await Query(roleMap.Keys.ToArray()).SingleOrDefaultAsync(item => item.Id == userId, cancellationToken);
        return row is null
            ? Result.Failure<PlatformAccessUser>("not_found", "Platform user was not found.")
            : Result.Success(ToUser(row, roleMap));
    }

    public async Task<Result<PlatformAccessGrant>> GrantAsync(GrantPlatformAccessCommand command, CancellationToken cancellationToken = default)
    {
        PlatformRoleDefinition? role = await FindRoleAsync(command.RoleKey, cancellationToken);
        if (role is null) return Result.Failure<PlatformAccessGrant>("invalid_role", "Choose a supported platform role.");

        string email = command.Email.Trim();
        string displayName = command.DisplayName.Trim();
        if (displayName.Length is < 2 or > 120)
            return Result.Failure<PlatformAccessGrant>("invalid_display_name", "Display name must be between 2 and 120 characters.");

        ApplicationUser? user = await users.FindByEmailAsync(email);
        bool created = user is null;
        if (created)
        {
            user = new ApplicationUser
            {
                Id = Guid.CreateVersion7(),
                Email = email,
                UserName = email,
                DisplayName = displayName,
                EmailConfirmed = false,
                LockoutEnabled = true,
                CreatedAt = DateTimeOffset.UtcNow
            };
            IdentityResult createResult = await users.CreateAsync(user);
            if (!createResult.Succeeded) return IdentityFailure<PlatformAccessGrant>(createResult);
        }
        else
        {
            IList<string> existingRoles = await users.GetRolesAsync(user!);
            if (await ContainsPlatformRoleAsync(existingRoles, cancellationToken))
                return Result.Failure<PlatformAccessGrant>("access_exists", "This identity already has platform access.");
            if (string.IsNullOrWhiteSpace(user!.DisplayName)) user.DisplayName = displayName;
        }

        user!.IsPlatformAccessSuspended = false;
        user.IsPlatformAdministrator = role.Key == PlatformRoles.Administrator;
        IdentityResult updateResult = await users.UpdateAsync(user);
        if (!updateResult.Succeeded) return IdentityFailure<PlatformAccessGrant>(updateResult);

        IdentityResult roleResult = await users.AddToRoleAsync(user, role.Key);
        if (!roleResult.Succeeded)
        {
            if (created) await users.DeleteAsync(user);
            return IdentityFailure<PlatformAccessGrant>(roleResult);
        }

        await users.UpdateSecurityStampAsync(user);
        string? activationToken = null;
        if (!user.EmailConfirmed || !await users.HasPasswordAsync(user))
        {
            string token = await users.GeneratePasswordResetTokenAsync(user);
            activationToken = EncodeToken(token);
        }

        PlatformAccessUser platformUser = (await GetAsync(user.Id, cancellationToken)).Value!;
        return Result.Success(new PlatformAccessGrant(platformUser, activationToken));
    }

    public Task<Result<PlatformAccessUser>> ChangeRoleAsync(Guid actorId, Guid userId, string roleKey, CancellationToken cancellationToken = default) =>
        ExecuteWithAdministrationLockAsync(() => ChangeRoleCoreAsync(actorId, userId, roleKey, cancellationToken));

    private async Task<Result<PlatformAccessUser>> ChangeRoleCoreAsync(Guid actorId, Guid userId, string roleKey, CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await BeginAdministrationTransactionAsync(cancellationToken);
        PlatformRoleDefinition? nextRole = await FindRoleAsync(roleKey, cancellationToken);
        if (nextRole is null) return Result.Failure<PlatformAccessUser>("invalid_role", "Choose a supported platform role.");
        ApplicationUser? user = await users.FindByIdAsync(userId.ToString());
        if (user is null) return Result.Failure<PlatformAccessUser>("not_found", "Platform user was not found.");

        string? currentRole = await FindAssignedPlatformRoleAsync(user, cancellationToken);
        if (currentRole is null) return Result.Failure<PlatformAccessUser>("not_found", "Platform user was not found.");
        if (actorId == userId && currentRole != roleKey)
            return Result.Failure<PlatformAccessUser>("self_change", "You cannot change your own platform role.");
        if (currentRole == PlatformRoles.Administrator && roleKey != PlatformRoles.Administrator && await IsLastActiveAdministratorAsync(userId, cancellationToken))
            return Result.Failure<PlatformAccessUser>("last_administrator", "At least one active platform administrator is required.");
        if (currentRole == roleKey)
        {
            Result<PlatformAccessUser> unchanged = await GetAsync(userId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return unchanged;
        }

        IdentityResult addResult = await users.AddToRoleAsync(user, roleKey);
        if (!addResult.Succeeded) return IdentityFailure<PlatformAccessUser>(addResult);
        IdentityResult removeResult = await users.RemoveFromRolesAsync(user, new[] { currentRole });
        if (!removeResult.Succeeded) return IdentityFailure<PlatformAccessUser>(removeResult);
        user.IsPlatformAdministrator = roleKey == PlatformRoles.Administrator;
        IdentityResult updateResult = await users.UpdateAsync(user);
        if (!updateResult.Succeeded) return IdentityFailure<PlatformAccessUser>(updateResult);
        await users.UpdateSecurityStampAsync(user);
        Result<PlatformAccessUser> changed = await GetAsync(userId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    public Task<Result<PlatformAccessUser>> SetStatusAsync(Guid actorId, Guid userId, bool isActive, CancellationToken cancellationToken = default) =>
        ExecuteWithAdministrationLockAsync(() => SetStatusCoreAsync(actorId, userId, isActive, cancellationToken));

    private async Task<Result<PlatformAccessUser>> SetStatusCoreAsync(Guid actorId, Guid userId, bool isActive, CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await BeginAdministrationTransactionAsync(cancellationToken);
        ApplicationUser? user = await users.FindByIdAsync(userId.ToString());
        if (user is null || await FindAssignedPlatformRoleAsync(user, cancellationToken) is null)
            return Result.Failure<PlatformAccessUser>("not_found", "Platform user was not found.");
        if (actorId == userId && !isActive)
            return Result.Failure<PlatformAccessUser>("self_change", "You cannot suspend your own platform access.");
        if (!isActive && await users.IsInRoleAsync(user, PlatformRoles.Administrator) && await IsLastActiveAdministratorAsync(userId, cancellationToken))
            return Result.Failure<PlatformAccessUser>("last_administrator", "At least one active platform administrator is required.");

        user.IsPlatformAccessSuspended = !isActive;
        IdentityResult result = await users.UpdateAsync(user);
        if (!result.Succeeded) return IdentityFailure<PlatformAccessUser>(result);
        await users.UpdateSecurityStampAsync(user);
        Result<PlatformAccessUser> changed = await GetAsync(userId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    public Task<Result<bool>> RevokeAsync(Guid actorId, Guid userId, CancellationToken cancellationToken = default) =>
        ExecuteWithAdministrationLockAsync(() => RevokeCoreAsync(actorId, userId, cancellationToken));

    private async Task<Result<bool>> RevokeCoreAsync(Guid actorId, Guid userId, CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await BeginAdministrationTransactionAsync(cancellationToken);
        ApplicationUser? user = await users.FindByIdAsync(userId.ToString());
        if (user is null) return Result.Failure<bool>("not_found", "Platform user was not found.");
        string[] currentRoles = await FindAssignedPlatformRolesAsync(user, cancellationToken);
        if (currentRoles.Length == 0) return Result.Failure<bool>("not_found", "Platform user was not found.");
        if (actorId == userId) return Result.Failure<bool>("self_change", "You cannot remove your own platform access.");
        if (currentRoles.Contains(PlatformRoles.Administrator, StringComparer.Ordinal) && await IsLastActiveAdministratorAsync(userId, cancellationToken))
            return Result.Failure<bool>("last_administrator", "At least one active platform administrator is required.");

        IdentityResult removeResult = await users.RemoveFromRolesAsync(user, currentRoles);
        if (!removeResult.Succeeded) return IdentityFailure<bool>(removeResult);
        user.IsPlatformAdministrator = false;
        user.IsPlatformAccessSuspended = false;
        IdentityResult updateResult = await users.UpdateAsync(user);
        if (!updateResult.Succeeded) return IdentityFailure<bool>(updateResult);
        await users.UpdateSecurityStampAsync(user);
        await transaction.CommitAsync(cancellationToken);
        return Result.Success(true);
    }

    public async Task<Result<string>> CreateActivationTokenAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        ApplicationUser? user = await users.FindByIdAsync(userId.ToString());
        if (user is null || await FindAssignedPlatformRoleAsync(user, cancellationToken) is null)
            return Result.Failure<string>("not_found", "Platform invitation was not found.");
        if (user.EmailConfirmed && await users.HasPasswordAsync(user))
            return Result.Failure<string>("already_active", "This platform user has already activated their account.");

        string token = await users.GeneratePasswordResetTokenAsync(user);
        return Result.Success(EncodeToken(token));
    }

    public async Task<Result<bool>> ActivateAsync(ActivatePlatformAccessCommand command, CancellationToken cancellationToken = default)
    {
        ApplicationUser? user = await users.FindByIdAsync(command.UserId.ToString());
        if (user is null || await FindAssignedPlatformRoleAsync(user, cancellationToken) is null)
            return Result.Failure<bool>("invalid_activation", "This activation link is invalid or has expired.");

        string token;
        try { token = DecodeToken(command.Token); }
        catch (FormatException) { return Result.Failure<bool>("invalid_activation", "This activation link is invalid or has expired."); }

        IdentityResult passwordResult = await users.ResetPasswordAsync(user, token, command.Password);
        if (!passwordResult.Succeeded) return IdentityFailure<bool>(passwordResult);
        user.EmailConfirmed = true;
        IdentityResult confirmResult = await users.UpdateAsync(user);
        return confirmResult.Succeeded ? Result.Success(true) : IdentityFailure<bool>(confirmResult);
    }

    private IQueryable<PlatformAccessRow> Query(string[] roleKeys) =>
        from user in database.Users.AsNoTracking()
        join userRole in database.UserRoles.AsNoTracking() on user.Id equals userRole.UserId
        join role in database.Roles.AsNoTracking() on userRole.RoleId equals role.Id
        where role.Name != null && roleKeys.Contains(role.Name)
        select new PlatformAccessRow
        {
            Id = user.Id,
            Email = user.Email ?? string.Empty,
            DisplayName = user.DisplayName,
            RoleKey = role.Name!,
            IsSuspended = user.IsPlatformAccessSuspended,
            EmailConfirmed = user.EmailConfirmed,
            CreatedAt = user.CreatedAt,
            LastSignedInAt = user.LastSignedInAt
        };

    private async Task<Result<NormalizedRoleInput>> ValidateRoleAsync(SavePlatformRoleCommand command, string? existingKey, CancellationToken cancellationToken)
    {
        string name = command.Name.Trim();
        string description = command.Description.Trim();
        if (name.Length is < 2 or > 80)
            return Result.Failure<NormalizedRoleInput>("invalid_role_name", "Role name must be between 2 and 80 characters.");
        if (description.Length > 240)
            return Result.Failure<NormalizedRoleInput>("invalid_role_description", "Role description cannot exceed 240 characters.");

        string[] requested = command.Permissions.Distinct(StringComparer.Ordinal).ToArray();
        string[] unknown = requested.Where(permission => !PlatformPermissions.All.Contains(permission)).ToArray();
        if (unknown.Length > 0)
            return Result.Failure<NormalizedRoleInput>("invalid_permissions", "One or more permissions are not part of the platform catalog.");

        HashSet<string> normalizedPermissions = new(requested, StringComparer.Ordinal) { PlatformPermissions.DashboardRead };
        foreach (string permission in requested.Where(permission => permission.EndsWith(".manage", StringComparison.Ordinal)))
        {
            normalizedPermissions.Add(permission[..^"manage".Length] + "read");
        }

        IReadOnlyList<PlatformRoleDefinition> definitions = await ListRolesAsync(cancellationToken);
        if (definitions.Any(role => role.Key != existingKey && string.Equals(role.Name, name, StringComparison.OrdinalIgnoreCase)))
            return Result.Failure<NormalizedRoleInput>("duplicate_role", "A platform role with this name already exists.");
        return Result.Success(new NormalizedRoleInput(name, description, normalizedPermissions));
    }

    private async Task<Result<bool>> ReplaceCustomRoleClaimsAsync(IdentityRole<Guid> role, NormalizedRoleInput input, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        IList<System.Security.Claims.Claim> existing = await roles.GetClaimsAsync(role);
        foreach (System.Security.Claims.Claim claim in existing.Where(claim => claim.Type is DisplayNameClaim or DescriptionClaim or PermissionClaim))
        {
            IdentityResult removed = await roles.RemoveClaimAsync(role, claim);
            if (!removed.Succeeded) return IdentityFailure<bool>(removed);
        }

        List<System.Security.Claims.Claim> claims =
        [
            new(DisplayNameClaim, input.Name),
            new(DescriptionClaim, input.Description),
            .. input.Permissions.Order(StringComparer.Ordinal).Select(permission => new System.Security.Claims.Claim(PermissionClaim, permission))
        ];
        foreach (System.Security.Claims.Claim claim in claims)
        {
            IdentityResult added = await roles.AddClaimAsync(role, claim);
            if (!added.Succeeded) return IdentityFailure<bool>(added);
        }
        return Result.Success(true);
    }

    private async Task<PlatformRoleDefinition> ToCustomDefinitionAsync(IdentityRole<Guid> role)
    {
        IList<System.Security.Claims.Claim> claims = await roles.GetClaimsAsync(role);
        string name = claims.FirstOrDefault(claim => claim.Type == DisplayNameClaim)?.Value ?? "Custom role";
        string description = claims.FirstOrDefault(claim => claim.Type == DescriptionClaim)?.Value ?? "Custom platform access.";
        IReadOnlySet<string> permissions = claims
            .Where(claim => claim.Type == PermissionClaim && PlatformPermissions.All.Contains(claim.Value))
            .Select(claim => claim.Value)
            .ToFrozenSet(StringComparer.Ordinal);
        return new PlatformRoleDefinition(role.Name!, name, description, 100, permissions, false);
    }

    private async Task<string[]> FindAssignedPlatformRolesAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        IList<string> assigned = await users.GetRolesAsync(user);
        IReadOnlyList<PlatformRoleDefinition> definitions = await ListRolesAsync(cancellationToken);
        HashSet<string> known = definitions.Select(role => role.Key).ToHashSet(StringComparer.Ordinal);
        return assigned.Where(known.Contains).ToArray();
    }

    private async Task<string?> FindAssignedPlatformRoleAsync(ApplicationUser user, CancellationToken cancellationToken) =>
        (await FindAssignedPlatformRolesAsync(user, cancellationToken)).FirstOrDefault();

    private async Task<bool> ContainsPlatformRoleAsync(IEnumerable<string> assigned, CancellationToken cancellationToken)
    {
        IReadOnlyList<PlatformRoleDefinition> definitions = await ListRolesAsync(cancellationToken);
        HashSet<string> known = definitions.Select(role => role.Key).ToHashSet(StringComparer.Ordinal);
        return assigned.Any(known.Contains);
    }

    private async Task InvalidateRoleMembersAsync(Guid roleId, CancellationToken cancellationToken)
    {
        Guid[] userIds = await database.UserRoles.Where(item => item.RoleId == roleId).Select(item => item.UserId).ToArrayAsync(cancellationToken);
        foreach (Guid userId in userIds)
        {
            ApplicationUser? user = await users.FindByIdAsync(userId.ToString());
            if (user is not null) await users.UpdateSecurityStampAsync(user);
        }
    }

    private static bool IsCustomRole(string roleKey) => roleKey.StartsWith(CustomRolePrefix, StringComparison.Ordinal);

    private async Task<bool> IsLastActiveAdministratorAsync(Guid userId, CancellationToken cancellationToken)
    {
        long otherActiveAdministrators = await (
            from user in database.Users.AsNoTracking()
            join userRole in database.UserRoles.AsNoTracking() on user.Id equals userRole.UserId
            join role in database.Roles.AsNoTracking() on userRole.RoleId equals role.Id
            where role.Name == PlatformRoles.Administrator && !user.IsPlatformAccessSuspended && user.Id != userId
            select user.Id).Distinct().LongCountAsync(cancellationToken);
        return otherActiveAdministrators == 0;
    }

    private async Task<IDbContextTransaction> BeginAdministrationTransactionAsync(CancellationToken cancellationToken)
    {
        IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(734001)", cancellationToken);
        return transaction;
    }

    private Task<Result<T>> ExecuteWithAdministrationLockAsync<T>(Func<Task<Result<T>>> operation) =>
        database.Database.CreateExecutionStrategy().ExecuteAsync(operation);

    private static PlatformAccessUser ToUser(PlatformAccessRow row, Dictionary<string, PlatformRoleDefinition> roleMap)
    {
        PlatformRoleDefinition role = roleMap[row.RoleKey];
        return new(row.Id, row.Email, row.DisplayName, row.RoleKey, role.Name, !row.IsSuspended, !row.EmailConfirmed, row.CreatedAt, row.LastSignedInAt);
    }

    private static Result<T> IdentityFailure<T>(IdentityResult result) =>
        Result.Failure<T>("identity_validation", string.Join(" ", result.Errors.Select(error => error.Description)));

    private static string EncodeToken(string token) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(token)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string DecodeToken(string token)
    {
        string value = token.Replace('-', '+').Replace('_', '/');
        value = value.PadRight(value.Length + (4 - value.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(value));
    }

    private sealed class PlatformAccessRow
    {
        public Guid Id { get; init; }
        public string Email { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string RoleKey { get; init; } = string.Empty;
        public bool IsSuspended { get; init; }
        public bool EmailConfirmed { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? LastSignedInAt { get; init; }
    }

    private sealed record NormalizedRoleInput(string Name, string Description, IReadOnlySet<string> Permissions);
}
