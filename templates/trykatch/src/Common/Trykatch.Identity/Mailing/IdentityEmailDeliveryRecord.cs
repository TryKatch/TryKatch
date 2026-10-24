using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Trykatch.Identity;

internal sealed class IdentityEmailDeliveryRecord
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string TemplateKey { get; set; }
    public int TemplateVersion { get; set; }
    public required string ProtectedIntent { get; set; }
    public required string State { get; set; }
    public int Attempts { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public DateTimeOffset? NextAttemptAtUtc { get; set; }
    public Guid? LastCompletedLeaseId { get; set; }
    public string? LastOutcome { get; set; }
    public string? LastFailureCode { get; set; }
    public int? LastSmtpStatusCode { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

internal static class IdentityEmailDeliveryMapping
{
    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<IdentityEmailDeliveryRecord>(ConfigureRecord);
    }

    private static void ConfigureRecord(EntityTypeBuilder<IdentityEmailDeliveryRecord> entity)
    {
        entity.ToTable("email_deliveries", "identity", table =>
        {
            table.HasCheckConstraint("CK_identity_email_deliveries_state",
                "\"State\" IN ('queued', 'leased', 'delivered', 'suppressed', 'terminal')");
            table.HasCheckConstraint("CK_identity_email_deliveries_attempts", "\"Attempts\" BETWEEN 0 AND 5");
            table.HasCheckConstraint("CK_identity_email_deliveries_template_version", "\"TemplateVersion\" > 0");
            table.HasCheckConstraint("CK_identity_email_deliveries_lease",
                "\"State\" <> 'leased' OR (\"LeaseId\" IS NOT NULL AND \"LeaseUntilUtc\" IS NOT NULL)");
        });
        entity.HasKey(row => row.Id);
        entity.Property(row => row.TemplateKey).HasMaxLength(160).IsRequired();
        entity.Property(row => row.ProtectedIntent).HasMaxLength(262144).IsRequired();
        entity.Property(row => row.State).HasMaxLength(16).IsRequired();
        entity.Property(row => row.LastOutcome).HasMaxLength(48);
        entity.Property(row => row.LastFailureCode).HasMaxLength(64);
        entity.HasIndex(row => new { row.State, row.NextAttemptAtUtc });
        entity.HasIndex(row => new { row.State, row.LeaseUntilUtc });
        entity.HasIndex(row => new { row.UserId, row.CreatedAtUtc });
    }
}
