using Microsoft.EntityFrameworkCore;
using Trykatch.Application.Auditing;
using Trykatch.Domain.Organizations;

namespace Trykatch.Infrastructure.Persistence;

public class PlatformDbContext : DbContext
{
    public PlatformDbContext(DbContextOptions<PlatformDbContext> options) : base(options) { }
    protected PlatformDbContext(DbContextOptions options) : base(options) { }

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<OrganizationCreationIntent> OrganizationCreationIntents => Set<OrganizationCreationIntent>();
    public DbSet<OrganizationDataPlacementRecord> OrganizationDataPlacements => Set<OrganizationDataPlacementRecord>();
    public DbSet<AuditIntent> AuditIntents => Set<AuditIntent>();
    public DbSet<OutboxReplayRequest> OutboxReplayRequests => Set<OutboxReplayRequest>();
    public DbSet<OutboxRecoveryEvent> OutboxRecoveryEvents => Set<OutboxRecoveryEvent>();

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

        modelBuilder.Entity<OrganizationDataPlacementRecord>(entity =>
        {
            entity.ToTable("organization_data_placements");
            entity.Ignore(x => x.Id);
            entity.HasKey(x => x.OrganizationId);
            entity.Property(x => x.Provider).HasMaxLength(80);
            entity.Property(x => x.RegionOrStamp).HasMaxLength(120);
            entity.Property(x => x.DatabaseIdentifier).HasMaxLength(120);
            entity.Property(x => x.SecretReference).HasMaxLength(500);
            entity.Property(x => x.SchemaVersion).HasMaxLength(80);
            entity.Property(x => x.FailureCode).HasMaxLength(120);
            entity.Property(x => x.Placement).HasConversion<int>();
            entity.Property(x => x.State).HasConversion<int>();
            entity.HasIndex(x => new { x.State, x.UpdatedAt });
        });

        modelBuilder.Entity<OrganizationCreationIntent>(entity =>
        {
            entity.ToTable("organization_creation_intents");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Slug).HasMaxLength(63);
            entity.Property(x => x.AdministratorEmail).HasMaxLength(320);
            entity.Property(x => x.IdempotencyIdentityHash).HasMaxLength(64).IsFixedLength();
            entity.Property(x => x.ProtectedInvitationToken).HasMaxLength(4096);
            entity.Property(x => x.Placement).HasConversion<int>();
            entity.HasIndex(x => x.OrganizationId).IsUnique();
            entity.HasIndex(x => x.Slug).IsUnique();
            entity.HasIndex(x => x.InvitationId).IsUnique();
            entity.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Invitation>().WithMany().HasForeignKey(x => x.InvitationId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AuditIntent>(entity =>
        {
            entity.ToTable("audit_intents");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Operation).HasMaxLength(120);
            entity.Property(x => x.SubjectType).HasMaxLength(120);
            entity.Property(x => x.SubjectId).HasMaxLength(160);
            entity.Property(x => x.SubjectDisplayName).HasMaxLength(240);
            entity.Property(x => x.Details).HasColumnType("jsonb").HasMaxLength(2048);
            entity.HasIndex(x => new { x.OrganizationId, x.OccurredAt, x.Id });
        });

        modelBuilder.Entity<OutboxReplayRequest>(entity =>
        {
            entity.ToTable("outbox_replay_requests", table => table.ExcludeFromMigrations());
            entity.HasKey(x => x.RequestId);
            entity.Property(x => x.RequestedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasIndex(x => new { x.RequestedAt, x.RequestId });
        });

        modelBuilder.Entity<OutboxRecoveryEvent>(entity =>
        {
            entity.ToTable("outbox_recovery_events", table => table.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Outcome).HasMaxLength(40);
            entity.Property(x => x.FailureCode).HasMaxLength(80);
            entity.Property(x => x.FailureType).HasMaxLength(240);
            entity.HasIndex(x => x.RequestId).IsUnique().HasFilter("\"RequestId\" IS NOT NULL");
            entity.HasIndex(x => new { x.MessageId, x.ReplayGeneration, x.Outcome }).IsUnique()
                .HasFilter("\"Outcome\" = 'terminal'");
            entity.HasIndex(x => new { x.OccurredAt, x.MessageId, x.ReplayGeneration });
        });
    }
}
