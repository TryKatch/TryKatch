using FlatpackApp.Domain.Organizations;
using FlatpackApp.Domain.Projects;
using Microsoft.EntityFrameworkCore;

namespace FlatpackApp.Infrastructure.Persistence;

public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Project>(entity =>
        {
            entity.ToTable("projects", "app");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(120);
            entity.Property(x => x.Description).HasMaxLength(2000);
            entity.HasIndex(x => new { x.OrganizationId, x.Name });
            entity.Property(x => x.DeletionReason).HasMaxLength(500);
            entity.HasQueryFilter(x => x.ArchivedAt == null && x.DeletedAt == null);
        });

        modelBuilder.Entity<AuditEntry>(entity =>
        {
            entity.ToTable("audit_entries", "platform");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Action).HasMaxLength(120);
            entity.Property(x => x.SubjectType).HasMaxLength(120);
            entity.Property(x => x.SubjectId).HasMaxLength(160);
            entity.Property(x => x.SubjectDisplayName).HasMaxLength(240);
            entity.Property(x => x.Details).HasColumnType("jsonb");
            entity.HasIndex(x => new { x.OrganizationId, x.OccurredAt });
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("outbox_messages", "platform");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Type).HasMaxLength(500);
            entity.Property(x => x.Payload).HasColumnType("jsonb");
            entity.HasIndex(x => new { x.ProcessedAt, x.OccurredAt });
        });
    }
}
