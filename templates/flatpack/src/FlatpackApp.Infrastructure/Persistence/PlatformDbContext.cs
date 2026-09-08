using FlatpackApp.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace FlatpackApp.Infrastructure.Persistence;

public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Invitation> Invitations => Set<Invitation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("platform");

        modelBuilder.Entity<Organization>(entity =>
        {
            entity.ToTable("organizations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(160);
            entity.Property(x => x.Slug).HasMaxLength(63);
            entity.HasIndex(x => x.Slug).IsUnique();
        });

        modelBuilder.Entity<Membership>(entity =>
        {
            entity.ToTable("memberships");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.OrganizationId, x.UserId }).IsUnique();
            entity.HasMany(x => x.Roles).WithOne().HasForeignKey(x => x.MembershipId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(x => x.DeletionReason).HasMaxLength(500);
        });

        modelBuilder.Entity<MembershipRole>(entity =>
        {
            entity.ToTable("membership_roles");
            entity.HasKey(x => new { x.MembershipId, x.RoleId });
        });

        modelBuilder.Entity<Role>(entity =>
        {
            entity.ToTable("roles");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(80);
            entity.Property(x => x.Description).HasMaxLength(240);
            entity.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique();
            entity.HasMany(x => x.Permissions).WithOne().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(x => x.DeletionReason).HasMaxLength(500);
        });

        modelBuilder.Entity<RolePermissionGrant>(entity =>
        {
            entity.ToTable("role_permissions");
            entity.HasKey(x => new { x.RoleId, x.Permission });
            entity.Property(x => x.Permission).HasMaxLength(120);
        });

        modelBuilder.Entity<Invitation>(entity =>
        {
            entity.ToTable("invitations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Email).HasMaxLength(320);
            entity.Property(x => x.TokenHash).HasMaxLength(128);
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(x => x.DeletionReason).HasMaxLength(500);
        });
    }
}
