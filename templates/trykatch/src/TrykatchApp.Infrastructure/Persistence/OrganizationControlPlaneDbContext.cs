using Microsoft.EntityFrameworkCore;

namespace TrykatchApp.Infrastructure.Persistence;

/// <summary>
/// Organization/actor-scoped access to shared control-plane records. This context
/// never uses the privileged platform connection, including during membership resolution.
/// </summary>
public sealed class OrganizationControlPlaneDbContext(DbContextOptions<OrganizationControlPlaneDbContext> options)
    : PlatformDbContext(options);
