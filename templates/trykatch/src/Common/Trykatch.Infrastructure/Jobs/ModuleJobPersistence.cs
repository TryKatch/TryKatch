using Microsoft.EntityFrameworkCore;

namespace Trykatch.Infrastructure.Jobs;

internal sealed class ModuleScheduleCursor
{
    public string ScheduleKey { get; init; } = string.Empty;
    public string TargetKey { get; init; } = string.Empty;
    public Guid OrganizationId { get; init; }
    public string ConfigurationHash { get; init; } = string.Empty;
    public DateTimeOffset StartAtUtc { get; init; }
    public DateTimeOffset ReconciledThroughUtc { get; set; }
    public long SkippedOccurrences { get; set; }
}

/// <summary>Append-only operational audit and idempotency receipt for a platform control.</summary>
internal sealed class ModuleWorkControl
{
    public Guid RequestId { get; init; }
    public Guid WorkId { get; init; }
    public Guid ActorId { get; init; }
    public string Action { get; init; } = string.Empty;
    public int ExpectedGeneration { get; init; }
    public int ResultGeneration { get; init; }
    public bool OperatorPaused { get; init; }
    public string Outcome { get; init; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; init; }
}

internal static class ModuleJobPersistence
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ModuleScheduleCursor>(entity =>
        {
            entity.ToTable("module_schedule_cursors", "infrastructure", table =>
                table.HasCheckConstraint("CK_module_schedule_skipped", "\"SkippedOccurrences\" >= 0"));
            entity.HasKey(row => new { row.ScheduleKey, row.TargetKey, row.OrganizationId });
            entity.Property(row => row.ScheduleKey).HasMaxLength(120);
            entity.Property(row => row.TargetKey).HasMaxLength(120);
            entity.Property(row => row.ConfigurationHash).HasMaxLength(64);
        });
        modelBuilder.Entity<ModuleWorkControl>(entity =>
        {
            entity.ToTable("module_work_controls", "infrastructure", table =>
            {
                table.HasCheckConstraint("CK_module_control_action", "\"Action\" IN ('pause','resume','retry')");
                table.HasCheckConstraint("CK_module_control_generation", "\"ExpectedGeneration\" >= 0 AND \"ResultGeneration\" > \"ExpectedGeneration\"");
            });
            entity.HasKey(row => row.RequestId);
            entity.Property(row => row.Action).HasMaxLength(20);
            entity.Property(row => row.Outcome).HasMaxLength(40);
            entity.HasIndex(row => new { row.WorkId, row.OccurredAtUtc });
        });
    }
}
