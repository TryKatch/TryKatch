using Microsoft.EntityFrameworkCore;

namespace Trykatch.Infrastructure.Quotas;

internal enum QuotaMaintenanceKind { TransactionOnlyExpiry = 1, UploadFencing = 2, Inventory = 3 }
internal enum QuotaMaintenanceState { Queued, Leased, Paused, Completed }

/// <summary>Closed recovery references, never a module handler, actor override or object-storage command.</summary>
internal sealed class QuotaMaintenanceWorkRecord
{
    public Guid OrganizationId { get; init; }
    public Guid Id { get; init; }
    public QuotaMaintenanceKind Kind { get; init; }
    public string ResourceKey { get; init; } = string.Empty;
    public Guid? ReservationId { get; init; }
    public Guid? UploadOperationId { get; init; }
    public QuotaMaintenanceState State { get; set; }
    public DateTimeOffset DueAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public int Attempts { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public string? LastFailureCode { get; set; }

    internal static void Configure(ModelBuilder model)
    {
        const string nonempty = "'00000000-0000-0000-0000-000000000000'::uuid";
        model.Entity<QuotaMaintenanceWorkRecord>(entity =>
        {
            entity.ToTable("quota_maintenance_work", "infrastructure", table =>
            {
                table.HasCheckConstraint("CK_quota_maintenance_work_identity", "\"OrganizationId\" <> " + nonempty
                    + " AND \"Id\" <> " + nonempty + """

                    AND "ResourceKey" ~ '^[a-z][a-z0-9]*([.-][a-z0-9]+)*$'
                    AND "ResourceKey" !~ '[[:space:]]' AND strpos("ResourceKey", '.') > 0
                    """);
                table.HasCheckConstraint("CK_quota_maintenance_work_reference", "\"Kind\" IN (1, 2, 3) AND ("
                    + "(\"Kind\" = 1 AND \"ReservationId\" IS NOT NULL AND \"ReservationId\" <> " + nonempty
                    + " AND \"UploadOperationId\" IS NULL) OR "
                    + "(\"Kind\" = 2 AND \"ReservationId\" IS NOT NULL AND \"ReservationId\" <> " + nonempty
                    + " AND \"UploadOperationId\" IS NOT NULL AND \"UploadOperationId\" <> " + nonempty + ") OR "
                    + "(\"Kind\" = 3 AND \"ReservationId\" IS NULL AND \"UploadOperationId\" IS NULL))");
                table.HasCheckConstraint("CK_quota_maintenance_work_state", """
                    "State" IN (0, 1, 2, 3) AND "Attempts" >= 0
                    AND (("State" = 1 AND "LeaseId" IS NOT NULL AND "LeaseId" <>
                    """ + nonempty + """
                     AND "LeaseUntilUtc" IS NOT NULL AND isfinite("LeaseUntilUtc"))
                    OR ("State" <> 1 AND "LeaseId" IS NULL AND "LeaseUntilUtc" IS NULL))
                    """);
                table.HasCheckConstraint("CK_quota_maintenance_work_schedule", """
                    isfinite("DueAtUtc") AND isfinite("CreatedAtUtc")
                    AND ("LastFailureCode" IS NULL OR ("LastFailureCode" ~ '^[a-z][a-z0-9_]{0,79}$'
                        AND "LastFailureCode" !~ '[[:space:]]'))
                    """);
            });
            entity.HasKey(row => new { row.OrganizationId, row.Id });
            entity.Property(row => row.OrganizationId).ValueGeneratedNever();
            entity.Property(row => row.Id).ValueGeneratedNever();
            entity.Property(row => row.ResourceKey).HasMaxLength(120);
            entity.Property(row => row.LastFailureCode).HasMaxLength(80);
            entity.HasOne<OrganizationEntitlementRecord>().WithMany()
                .HasForeignKey(row => new { row.OrganizationId, row.ResourceKey }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<OrganizationQuotaReservationRecord>().WithMany()
                .HasForeignKey(row => new { row.OrganizationId, row.ReservationId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ModuleUploadOperationRecord>().WithMany()
                .HasForeignKey(row => new { row.OrganizationId, row.ResourceKey, row.UploadOperationId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(row => new { row.OrganizationId, row.Kind, row.ResourceKey, row.ReservationId }).IsUnique()
                .HasFilter("\"ReservationId\" IS NOT NULL");
            entity.HasIndex(row => new { row.OrganizationId, row.ResourceKey }).IsUnique()
                .HasFilter("\"Kind\" = 3 AND \"State\" IN (0, 1, 2)");
            entity.HasIndex(row => new { row.State, row.DueAtUtc, row.LeaseUntilUtc, row.OrganizationId, row.Id });
        });
    }
}
