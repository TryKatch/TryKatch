using TrykatchApp.Domain.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace TrykatchApp.Infrastructure.Persistence;

public sealed class ApplicationDbContext(
    DbContextOptions<ApplicationDbContext> options,
    IEnumerable<IApplicationModelContributor> modelContributors) : DbContext(options)
{
    private readonly IApplicationModelContributor[] contributors = modelContributors
        .OrderBy(contributor => contributor.ModuleId, StringComparer.Ordinal)
        .ToArray();

    internal string ModelCompositionKey => string.Join('|', contributors.Select(contributor => contributor.ModuleId));

    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : this(options, [])
    {
    }

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, ApplicationModelCacheKeyFactory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        string? duplicateModule = contributors
            .GroupBy(contributor => contributor.ModuleId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateModule is not null)
            throw new InvalidOperationException($"Duplicate application model contributor for module '{duplicateModule}'.");

        foreach (IApplicationModelContributor contributor in contributors)
            contributor.Configure(modelBuilder);

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
