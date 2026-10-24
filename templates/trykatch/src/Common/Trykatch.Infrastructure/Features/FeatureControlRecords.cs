using Microsoft.EntityFrameworkCore;
using Trykatch.Domain.Organizations;

namespace Trykatch.Infrastructure.Features;

internal sealed class FeatureEnvironmentControl
{
    public string Environment { get; set; } = string.Empty;
    public string FeatureKey { get; set; } = string.Empty;
    public bool? DefaultEnabled { get; set; }
    public bool ForceDisabled { get; set; }
    public Guid Version { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

internal sealed class OrganizationFeatureOverride
{
    public Guid OrganizationId { get; set; }
    public string Environment { get; set; } = string.Empty;
    public string FeatureKey { get; set; } = string.Empty;
    public bool? Enabled { get; set; }
    public Guid Version { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

internal sealed class FeatureControlHistory
{
    public Guid Id { get; set; }
    public string Environment { get; set; } = string.Empty;
    public string FeatureKey { get; set; } = string.Empty;
    public Guid? OrganizationId { get; set; }
    public Guid ActorId { get; set; }
    public Guid? PreviousVersion { get; set; }
    public Guid Version { get; set; }
    public bool? PreviousEnabled { get; set; }
    public bool? Enabled { get; set; }
    public bool? PreviousForceDisabled { get; set; }
    public bool? ForceDisabled { get; set; }
    public DateTimeOffset ChangedAtUtc { get; set; }
}

internal static class FeatureControlMapping
{
    private const string Identity = """
        "Environment" ~ '^[a-z0-9][a-z0-9._-]{0,63}$'
        AND "FeatureKey" ~ '^[a-z][a-z0-9]*([.-][a-z0-9]+)*$'
        AND strpos("FeatureKey", '.') > 0
        """;
    private const string Version = "\"Version\" <> '00000000-0000-0000-0000-000000000000'::uuid";

    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FeatureEnvironmentControl>(entity =>
        {
            entity.ToTable("feature_environment_controls", "platform", table =>
            {
                table.HasCheckConstraint("CK_feature_environment_controls_identity", Identity);
                table.HasCheckConstraint("CK_feature_environment_controls_version", Version);
                table.HasCheckConstraint("CK_feature_environment_controls_time", "isfinite(\"UpdatedAtUtc\")");
            });
            entity.HasKey(row => new { row.Environment, row.FeatureKey });
            entity.Property(row => row.Environment).HasMaxLength(64);
            entity.Property(row => row.FeatureKey).HasMaxLength(120);
            entity.Property(row => row.Version).IsConcurrencyToken().ValueGeneratedNever();
            entity.Property(row => row.UpdatedAtUtc).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });
        modelBuilder.Entity<OrganizationFeatureOverride>(entity =>
        {
            entity.ToTable("organization_feature_overrides", "platform", table =>
            {
                table.HasCheckConstraint("CK_organization_feature_overrides_identity", Identity);
                table.HasCheckConstraint("CK_organization_feature_overrides_version", Version);
                table.HasCheckConstraint("CK_organization_feature_overrides_organization",
                    "\"OrganizationId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                table.HasCheckConstraint("CK_organization_feature_overrides_time", "isfinite(\"UpdatedAtUtc\")");
            });
            entity.HasKey(row => new { row.OrganizationId, row.Environment, row.FeatureKey });
            entity.Property(row => row.Environment).HasMaxLength(64);
            entity.Property(row => row.FeatureKey).HasMaxLength(120);
            entity.Property(row => row.Version).IsConcurrencyToken().ValueGeneratedNever();
            entity.Property(row => row.UpdatedAtUtc).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasOne<Organization>().WithMany().HasForeignKey(row => row.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<FeatureControlHistory>(entity =>
        {
            entity.ToTable("feature_control_history", "platform", table =>
            {
                table.HasCheckConstraint("CK_feature_control_history_identity", Identity);
                table.HasCheckConstraint("CK_feature_control_history_principals", """
                    "Id" <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND "ActorId" <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND ("OrganizationId" IS NULL OR "OrganizationId" <> '00000000-0000-0000-0000-000000000000'::uuid)
                    """);
                table.HasCheckConstraint("CK_feature_control_history_versions", Version + """

                    AND ("PreviousVersion" IS NULL OR
                        ("PreviousVersion" <> '00000000-0000-0000-0000-000000000000'::uuid AND "PreviousVersion" <> "Version"))
                    """);
                table.HasCheckConstraint("CK_feature_control_history_shape", """
                    ("PreviousVersion" IS NOT NULL OR "PreviousEnabled" IS NULL)
                    AND (("OrganizationId" IS NULL AND "ForceDisabled" IS NOT NULL
                        AND (("PreviousVersion" IS NULL AND "PreviousForceDisabled" IS NULL)
                            OR ("PreviousVersion" IS NOT NULL AND "PreviousForceDisabled" IS NOT NULL)))
                        OR ("OrganizationId" IS NOT NULL AND "ForceDisabled" IS NULL AND "PreviousForceDisabled" IS NULL))
                    """);
                table.HasCheckConstraint("CK_feature_control_history_time", "isfinite(\"ChangedAtUtc\")");
            });
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).ValueGeneratedNever();
            entity.Property(row => row.Environment).HasMaxLength(64);
            entity.Property(row => row.FeatureKey).HasMaxLength(120);
            entity.Property(row => row.ChangedAtUtc).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasOne<Organization>().WithMany().HasForeignKey(row => row.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(row => new { row.Environment, row.FeatureKey, row.OrganizationId, row.ChangedAtUtc, row.Id });
        });
    }
}
