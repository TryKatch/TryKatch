using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Quotas;

internal enum QuotaReservationEffectKind { TransactionOnly = 1, Upload = 2 }
internal enum QuotaInventoryState { Initializing, Complete, Unavailable, Paused }
internal enum ModuleUploadState { Admitted, Committed, Fencing, Cancelled }

internal sealed class OrganizationEntitlementRecord : IOrganizationOwned
{
    public Guid OrganizationId { get; set; }
    public string ResourceKey { get; set; } = string.Empty;
    public string OwnerModuleId { get; set; } = string.Empty;
    // Display metadata only; owner, unit and operator publication remain the authority.
    public string? DisplayName { get; set; }
    public ModuleQuotaUnit Unit { get; set; }
    public OrganizationEntitlementMode Mode { get; set; }
    public long? Limit { get; set; }
    public Guid Version { get; set; }
    public string Source { get; set; } = string.Empty;
    public long SourceRevision { get; set; }
    public string SourceHash { get; set; } = string.Empty;
    public DateTimeOffset EffectiveAtUtc { get; set; }
}

internal sealed class OrganizationQuotaReservationRecord : IOrganizationOwned
{
    public Guid OrganizationId { get; set; }
    public Guid Id { get; set; }
    public string ResourceKey { get; set; } = string.Empty;
    public Guid ActorId { get; set; }
    public long Units { get; set; }
    public Guid OperationId { get; set; }
    public Guid EntitlementVersion { get; set; }
    public QuotaReservationEffectKind EffectKind { get; set; }
    public ModuleQuotaReservationState State { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? ClosedAtUtc { get; set; }
}

internal sealed class OrganizationUsageRecord : IOrganizationOwned
{
    public Guid OrganizationId { get; set; }
    public string ResourceKey { get; set; } = string.Empty;
    public Guid EffectId { get; set; }
    public Guid SourceId { get; set; }
    public Guid? SourceEffectId { get; set; }
    public Guid? ReservationId { get; set; }
    public Guid ActorId { get; set; }
    public long Units { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}

internal sealed class OrganizationQuotaInventoryRecord : IOrganizationOwned
{
    public Guid OrganizationId { get; set; }
    public string ResourceKey { get; set; } = string.Empty;
    public string? SourceKey { get; set; }
    public int? SourceVersion { get; set; }
    public string? Cursor { get; set; }
    public QuotaInventoryState State { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

internal sealed class ModuleUploadOperationRecord : IOrganizationOwned
{
    public Guid OrganizationId { get; set; }
    public string ResourceKey { get; set; } = string.Empty;
    public Guid OperationId { get; set; }
    public Guid ActorId { get; set; }
    public Guid MembershipId { get; set; }
    public Guid ReservationId { get; set; }
    public Guid SubjectId { get; set; }
    public string ObjectKey { get; set; } = string.Empty;
    public string StorageNamespaceFingerprint { get; set; } = string.Empty;
    public long Length { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string MetadataFingerprint { get; set; } = string.Empty;
    // Unbound historical rows retain their identity/hold; no policy is invented during migration.
    public string? UploadPolicyKey { get; set; }
    public int? UploadPolicyVersion { get; set; }
    public ModuleUploadState State { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? ClosedAtUtc { get; set; }
}

internal static class QuotaRecordMapping
{
    private const string Nonempty = "'00000000-0000-0000-0000-000000000000'::uuid";
    private const string Scope = "\"OrganizationId\" <> " + Nonempty + """

        AND "ResourceKey" ~ '^[a-z][a-z0-9]*([.-][a-z0-9]+)*$'
        AND "ResourceKey" !~ '[[:space:]]' AND strpos("ResourceKey", '.') > 0
        """;

    internal static void Configure(ModelBuilder model)
    {
        model.Entity<OrganizationEntitlementRecord>(entity =>
        {
            const string table = "organization_entitlements";
            Identity(entity);
            entity.ToTable(table, "app", definition =>
            {
                definition.HasCheckConstraint("CK_organization_entitlements_identity", Scope + """

                    AND "OwnerModuleId" ~ '^[a-z][a-z0-9]*(-[a-z0-9]+)*$'
                    AND "OwnerModuleId" !~ '[[:space:]]'
                    AND left("ResourceKey", length("OwnerModuleId") + 1) = "OwnerModuleId" || '.'
                    AND "Unit" IN (1, 2)
                    """);
                definition.HasCheckConstraint("CK_organization_entitlements_projection", """
                    ("Mode" = 1 AND "Limit" IS NOT NULL AND "Limit" >= 0)
                    OR ("Mode" IN (0, 2, 3) AND "Limit" IS NULL)
                    """);
                definition.HasCheckConstraint("CK_organization_entitlements_source",
                    "\"Version\" <> " + Nonempty + """

                    AND "SourceRevision" >= 0 AND "SourceHash" ~ '^[A-Fa-f0-9]{64}$'
                    AND length(btrim("Source")) > 0 AND isfinite("EffectiveAtUtc")
                    """);
            });
            entity.HasKey(row => new { row.OrganizationId, row.ResourceKey });
            entity.Property(row => row.OwnerModuleId).HasMaxLength(80);
            entity.Property(row => row.DisplayName).HasMaxLength(160);
            entity.Property(row => row.Source).HasMaxLength(160);
            entity.Property(row => row.SourceHash).HasMaxLength(64);
            entity.Property(row => row.Version).ValueGeneratedNever().IsConcurrencyToken();
        });

        model.Entity<OrganizationQuotaReservationRecord>(entity =>
        {
            Identity(entity);
            entity.ToTable("organization_quota_reservations", "app", definition =>
            {
                definition.HasCheckConstraint("CK_organization_quota_reservations_identity", Scope
                    + " AND \"Id\" <> " + Nonempty + " AND \"ActorId\" <> " + Nonempty
                    + " AND \"OperationId\" <> " + Nonempty + " AND \"EntitlementVersion\" <> " + Nonempty);
                definition.HasCheckConstraint("CK_organization_quota_reservations_shape", """
                    "Units" > 0 AND "EffectKind" IN (1, 2) AND "State" IN (0, 1, 2)
                    AND isfinite("CreatedAtUtc") AND isfinite("ExpiresAtUtc") AND "ExpiresAtUtc" > "CreatedAtUtc"
                    AND (("State" = 0 AND "ClosedAtUtc" IS NULL) OR
                        ("State" IN (1, 2) AND "ClosedAtUtc" IS NOT NULL
                            AND isfinite("ClosedAtUtc") AND "ClosedAtUtc" >= "CreatedAtUtc"))
                    """);
            });
            entity.HasKey(row => new { row.OrganizationId, row.Id });
            entity.Property(row => row.Id).ValueGeneratedNever();
            entity.HasIndex(row => new { row.OrganizationId, row.ResourceKey, row.OperationId }).IsUnique();
            entity.HasIndex(row => new { row.OrganizationId, row.ResourceKey, row.State });
            entity.HasOne<OrganizationEntitlementRecord>().WithMany()
                .HasForeignKey(row => new { row.OrganizationId, row.ResourceKey }).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<OrganizationUsageRecord>(entity =>
        {
            Identity(entity);
            entity.ToTable("organization_usage", "app", definition =>
            {
                definition.HasCheckConstraint("CK_organization_usage_identity", Scope
                    + " AND \"EffectId\" <> " + Nonempty + " AND \"SourceId\" <> " + Nonempty
                    + " AND \"ActorId\" <> " + Nonempty);
                definition.HasCheckConstraint("CK_organization_usage_effect", """
                    "Units" <> 0 AND "Units" <> '-9223372036854775808'::bigint
                    AND isfinite("OccurredAtUtc")
                    AND (("Units" > 0 AND "SourceEffectId" IS NULL)
                        OR ("Units" < 0 AND "SourceEffectId" IS NOT NULL AND "SourceEffectId" <> "EffectId"))
                    """);
            });
            entity.HasKey(row => new { row.OrganizationId, row.ResourceKey, row.EffectId });
            entity.Property(row => row.EffectId).ValueGeneratedNever();
            entity.HasIndex(row => new { row.OrganizationId, row.ResourceKey, row.SourceEffectId });
            entity.HasOne<OrganizationEntitlementRecord>().WithMany()
                .HasForeignKey(row => new { row.OrganizationId, row.ResourceKey }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<OrganizationQuotaReservationRecord>().WithMany()
                .HasForeignKey(row => new { row.OrganizationId, row.ReservationId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<OrganizationUsageRecord>().WithMany()
                .HasForeignKey(row => new { row.OrganizationId, row.ResourceKey, row.SourceEffectId })
                .OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<OrganizationQuotaInventoryRecord>(entity =>
        {
            Identity(entity);
            entity.ToTable("organization_quota_inventory", "app", definition =>
            {
                definition.HasCheckConstraint("CK_organization_quota_inventory_identity", Scope);
                definition.HasCheckConstraint("CK_organization_quota_inventory_progress", """
                    "State" IN (0, 1, 2, 3) AND isfinite("UpdatedAtUtc")
                    AND (("SourceKey" IS NULL AND "SourceVersion" IS NULL AND "State" <> 1)
                        OR ("SourceKey" IS NOT NULL AND "SourceVersion" IS NOT NULL AND "SourceVersion" > 0
                            AND "SourceKey" ~ '^[a-z][a-z0-9]*([.-][a-z0-9]+)*$'
                            AND "SourceKey" !~ '[[:space:]]'))
                    """);
            });
            entity.HasKey(row => new { row.OrganizationId, row.ResourceKey });
            entity.Property(row => row.SourceKey).HasMaxLength(120);
            entity.Property(row => row.Cursor).HasMaxLength(1024);
            entity.HasOne<OrganizationEntitlementRecord>().WithMany()
                .HasForeignKey(row => new { row.OrganizationId, row.ResourceKey }).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<ModuleUploadOperationRecord>(entity =>
        {
            Identity(entity);
            entity.ToTable("module_upload_operations", "app", definition =>
            {
                definition.HasCheckConstraint("CK_module_upload_operations_identity", Scope
                    + " AND \"OperationId\" <> " + Nonempty + " AND \"ActorId\" <> " + Nonempty
                    + " AND \"MembershipId\" <> " + Nonempty + " AND \"ReservationId\" <> " + Nonempty
                    + " AND \"SubjectId\" <> " + Nonempty);
                definition.HasCheckConstraint("CK_module_upload_operations_payload", """
                    "Length" > 0 AND length("ObjectKey") > 0
                    AND "Sha256" ~ '^[A-Fa-f0-9]{64}$' AND "MetadataFingerprint" ~ '^[A-Fa-f0-9]{64}$'
                    AND "StorageNamespaceFingerprint" ~ '^[A-Fa-f0-9]{64}$'
                    """);
                definition.HasCheckConstraint("CK_module_upload_operations_policy", """
                    ("UploadPolicyKey" IS NULL AND "UploadPolicyVersion" IS NULL)
                    OR ("UploadPolicyKey" IS NOT NULL AND "UploadPolicyVersion" IS NOT NULL AND "UploadPolicyVersion" > 0
                        AND "UploadPolicyKey" ~ '^[a-z][a-z0-9]*([.-][a-z0-9]+)*$'
                        AND "UploadPolicyKey" !~ '[[:space:]]'
                        AND left("UploadPolicyKey", length(split_part("ResourceKey", '.', 1)) + 1)
                            = split_part("ResourceKey", '.', 1) || '.')
                    """);
                definition.HasCheckConstraint("CK_module_upload_operations_state", """
                    "State" IN (0, 1, 2, 3) AND isfinite("CreatedAtUtc") AND isfinite("ExpiresAtUtc")
                    AND "ExpiresAtUtc" > "CreatedAtUtc"
                    AND (("State" IN (0, 2) AND "ClosedAtUtc" IS NULL) OR
                        ("State" IN (1, 3) AND "ClosedAtUtc" IS NOT NULL
                            AND isfinite("ClosedAtUtc") AND "ClosedAtUtc" >= "CreatedAtUtc"))
                    """);
            });
            entity.HasKey(row => new { row.OrganizationId, row.ResourceKey, row.OperationId });
            entity.Property(row => row.ObjectKey).HasMaxLength(500);
            entity.Property(row => row.Sha256).HasMaxLength(64);
            entity.Property(row => row.MetadataFingerprint).HasMaxLength(64);
            entity.Property(row => row.UploadPolicyKey).HasMaxLength(120);
            entity.Property(row => row.StorageNamespaceFingerprint).HasMaxLength(64);
            entity.HasIndex(row => new { row.OrganizationId, row.ObjectKey }).IsUnique();
            entity.HasIndex(row => new { row.OrganizationId, row.SubjectId }).IsUnique();
            entity.HasIndex(row => new { row.OrganizationId, row.ReservationId }).IsUnique();
            entity.HasOne<OrganizationEntitlementRecord>().WithMany()
                .HasForeignKey(row => new { row.OrganizationId, row.ResourceKey }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<OrganizationQuotaReservationRecord>().WithMany()
                .HasForeignKey(row => new { row.OrganizationId, row.ReservationId }).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void Identity<T>(EntityTypeBuilder<T> entity) where T : class, IOrganizationOwned
    {
        entity.Property(row => row.OrganizationId).ValueGeneratedNever();
        entity.Property<string>("ResourceKey").HasMaxLength(120);
    }
}
