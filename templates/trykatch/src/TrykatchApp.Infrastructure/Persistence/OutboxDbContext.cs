using Microsoft.EntityFrameworkCore;

namespace TrykatchApp.Infrastructure.Persistence;

internal sealed class OutboxDbContext(DbContextOptions<OutboxDbContext> options) : DbContext(options)
{
    public DbSet<OutboxMessage> Messages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("outbox_messages", "platform", table => table.ExcludeFromMigrations());
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Type).HasMaxLength(500);
            entity.Property(x => x.Payload).HasColumnType("jsonb");
            entity.HasIndex(x => new { x.ProcessedAt, x.OccurredAt });
        });
    }
}
