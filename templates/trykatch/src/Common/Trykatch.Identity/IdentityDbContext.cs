using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Trykatch.Identity;

public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options), IDataProtectionKeyContext
{
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema("identity");
        builder.UseOpenIddict();
        builder.Entity<ApplicationUser>().Property(x => x.DisplayName).HasMaxLength(160);
        builder.Entity<ApplicationUser>().Property(x => x.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        builder.Entity<DataProtectionKey>().ToTable("data_protection_keys", "identity");
        builder.Entity<RecentAssuranceRecord>(entity =>
        {
            entity.ToTable("recent_assurance_grants");
            entity.HasKey(value => value.TokenHash);
            entity.Property(value => value.TokenHash).HasMaxLength(64);
            entity.Property(value => value.SessionId).HasMaxLength(64);
            entity.Property(value => value.SecurityStamp).HasMaxLength(256);
            entity.Property(value => value.Purpose).HasMaxLength(64);
            entity.HasIndex(value => new { value.UserId, value.ExpiresAt });
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(value => value.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<PendingMfaEnrollment>(entity =>
        {
            entity.ToTable("pending_mfa_enrollments");
            entity.HasKey(value => value.UserId);
            entity.Property(value => value.SessionId).HasMaxLength(64);
            entity.Property(value => value.SecurityStamp).HasMaxLength(256);
            entity.HasOne<ApplicationUser>().WithOne().HasForeignKey<PendingMfaEnrollment>(value => value.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<AccountSecurityEvent>(entity =>
        {
            entity.ToTable("account_security_events");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Action).HasMaxLength(64);
            entity.Property(value => value.Outcome).HasMaxLength(64);
            entity.HasIndex(value => new { value.UserId, value.OccurredAt });
        });
        builder.Entity<IdentitySessionRecord>(entity =>
        {
            entity.ToTable("account_sessions", table =>
            {
                table.HasCheckConstraint("CK_account_sessions_hash", "\"SessionHash\" ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint("CK_account_sessions_stamp", "length(\"SecurityStamp\") > 0");
                table.HasCheckConstraint("CK_account_sessions_generation", "\"WorkspaceGeneration\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                table.HasCheckConstraint("CK_account_sessions_organization", "\"OrganizationId\" IS NULL OR \"OrganizationId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                table.HasCheckConstraint("CK_account_sessions_expiry", "\"ExpiresAt\" > \"CreatedAt\"");
            });
            entity.HasKey(value => value.SessionHash);
            entity.Property(value => value.SessionHash).HasMaxLength(64);
            entity.Property(value => value.SecurityStamp).HasMaxLength(256);
            entity.HasIndex(value => new { value.UserId, value.ExpiresAt });
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(value => value.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        IdentityEmailDeliveryMapping.Configure(builder);
    }
}
