using Microsoft.EntityFrameworkCore;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Execution;

internal sealed class ModuleWorkItem
{
    public Guid Id { get; init; }
    public Guid OrganizationId { get; init; }
    public Guid ActorId { get; init; }
    public Guid RequestId { get; init; }
    public string ExecutionIdentityKey { get; init; } = string.Empty;
    public string JobKey { get; init; } = string.Empty;
    public int JobVersion { get; init; }
    public string OperationKey { get; init; } = string.Empty;
    public int OperationVersion { get; init; }
    public int ContextVersion { get; init; }
    public string ExecutionContextJson { get; init; } = string.Empty;
    public string Payload { get; init; } = string.Empty;
    public string PayloadHash { get; init; } = string.Empty;
    public string IntentHash { get; init; } = string.Empty;
    public string SubmissionMode { get; init; } = string.Empty;
    public DateTimeOffset? DueAtUtc { get; init; }
    public string State { get; set; } = "queued";
    public int Attempts { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public string? LastFailureCode { get; set; }
    public DateTimeOffset CreatedAtUtc { get; init; }
}

internal sealed class ModuleEventDelivery
{
    public Guid Id { get; init; }
    public Guid MessageId { get; init; }
    public Guid OrganizationId { get; init; }
    public string ConsumerKey { get; init; } = string.Empty;
    public string OperationKey { get; init; } = string.Empty;
    public int OperationVersion { get; init; }
    public int SchemaVersion { get; init; }
    public string PayloadHash { get; init; } = string.Empty;
    public string State { get; set; } = "queued";
    public int Attempts { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public string? LastFailureCode { get; set; }
    public int ReplayGeneration { get; set; }
}

internal sealed class ModuleEventInbox : IOrganizationOwned
{
    public Guid OrganizationId { get; init; }
    public Guid MessageId { get; init; }
    public string ConsumerKey { get; init; } = string.Empty;
    public int SchemaVersion { get; init; }
    public Guid ActorId { get; init; }
    public DateTimeOffset ProcessedAtUtc { get; init; }
}

internal static class ModuleWorkPersistence
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ModuleWorkItem>(entity =>
        {
            entity.ToTable("module_work_items", "infrastructure", table =>
            {
                table.HasCheckConstraint("CK_module_work_versions", "\"JobVersion\" > 0 AND \"OperationVersion\" > 0 AND \"ContextVersion\" > 0 AND \"Attempts\" >= 0");
                table.HasCheckConstraint("CK_module_work_submission", "(\"SubmissionMode\" = 'enqueue' AND \"DueAtUtc\" IS NULL) OR (\"SubmissionMode\" = 'schedule' AND \"DueAtUtc\" IS NOT NULL)");
                table.HasCheckConstraint("CK_module_work_state", "\"State\" IN ('queued', 'leased', 'paused', 'completed', 'denied', 'terminal')");
                table.HasCheckConstraint("CK_module_work_payload", "octet_length(\"Payload\"::text) <= 1048576");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ExecutionIdentityKey).HasMaxLength(120);
            entity.Property(x => x.JobKey).HasMaxLength(120);
            entity.Property(x => x.OperationKey).HasMaxLength(120);
            entity.Property(x => x.ExecutionContextJson).HasColumnType("jsonb");
            entity.Property(x => x.Payload).HasColumnType("jsonb");
            entity.Property(x => x.PayloadHash).HasMaxLength(64);
            entity.Property(x => x.IntentHash).HasMaxLength(64);
            entity.Property(x => x.SubmissionMode).HasMaxLength(20);
            entity.Property(x => x.State).HasMaxLength(20);
            entity.Property(x => x.LastFailureCode).HasMaxLength(80);
            entity.HasIndex(x => new { x.OrganizationId, x.ExecutionIdentityKey, x.JobKey, x.JobVersion, x.RequestId }).IsUnique();
            entity.HasIndex(x => new { x.State, x.DueAtUtc, x.LeaseUntilUtc, x.Id });
        });
        modelBuilder.Entity<ModuleEventDelivery>(entity =>
        {
            entity.ToTable("module_event_deliveries", "infrastructure", table =>
            {
                table.HasCheckConstraint("CK_module_delivery_versions", "\"OperationVersion\" > 0 AND \"SchemaVersion\" > 0 AND \"Attempts\" >= 0 AND \"ReplayGeneration\" >= 0");
                table.HasCheckConstraint("CK_module_delivery_state", "\"State\" IN ('queued', 'leased', 'paused', 'completed', 'terminal')");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ConsumerKey).HasMaxLength(120);
            entity.Property(x => x.OperationKey).HasMaxLength(120);
            entity.Property(x => x.PayloadHash).HasMaxLength(64);
            entity.Property(x => x.State).HasMaxLength(20);
            entity.Property(x => x.LastFailureCode).HasMaxLength(80);
            entity.HasIndex(x => new { x.MessageId, x.ConsumerKey }).IsUnique();
            entity.HasIndex(x => new { x.State, x.LeaseUntilUtc, x.Id });
        });
        modelBuilder.Entity<ModuleEventInbox>(entity =>
        {
            entity.ToTable("module_event_inbox", "app", table =>
                table.HasCheckConstraint("CK_module_inbox_version", "\"SchemaVersion\" > 0"));
            entity.HasKey(x => new { x.OrganizationId, x.MessageId, x.ConsumerKey });
            entity.Property(x => x.ConsumerKey).HasMaxLength(120);
            entity.HasIndex(x => new { x.OrganizationId, x.ProcessedAtUtc });
        });
    }
}
