using Microsoft.EntityFrameworkCore;

namespace Trykatch.Infrastructure.Persistence;

internal sealed class OutboxDbContext(DbContextOptions<OutboxDbContext> options) : DbContext(options)
{
    public DbSet<OutboxMessage> Messages => Set<OutboxMessage>();
    public DbSet<OutboxReplayRequest> ReplayRequests => Set<OutboxReplayRequest>();
    public DbSet<OutboxRecoveryEvent> RecoveryEvents => Set<OutboxRecoveryEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("outbox_messages", "platform", table => table.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Type).HasMaxLength(500);
            entity.Property(x => x.Payload).HasColumnType("jsonb");
            entity.Property(x => x.LastErrorCode).HasMaxLength(80);
            entity.Property(x => x.LastErrorType).HasMaxLength(500);
            entity.HasIndex(x => new { x.ProcessedAt, x.ExhaustedAt, x.OccurredAt, x.Id });
        });
        modelBuilder.Entity<OutboxReplayRequest>(entity =>
        {
            entity.ToTable("outbox_replay_requests", "platform", table => table.ExcludeFromMigrations());
            entity.HasKey(x => x.RequestId);
            entity.Property(x => x.RequestedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasIndex(x => new { x.RequestedAt, x.RequestId });
        });
        modelBuilder.Entity<OutboxRecoveryEvent>(entity =>
        {
            entity.ToTable("outbox_recovery_events", "platform", table => table.ExcludeFromMigrations());
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
