using Microsoft.EntityFrameworkCore;

namespace Trykatch.Infrastructure.Mailing;

/// <summary>Host operational metadata; disclosure authority and private content remain in the protected intent.</summary>
internal sealed class OrganizationEmailDeliveryRecord
{
    public Guid Id { get; init; }
    public Guid OrganizationId { get; init; }
    public Guid ActorId { get; init; }
    // Retained storage column: logical dedup ID. The distinct caller message ID belongs in the protected intent.
    public Guid MessageId { get; init; }
    public required string AdmissionIdentityKey { get; init; }
    public required string TemplateKey { get; init; }
    public int TemplateVersion { get; init; }
    public required string ProtectedIntent { get; init; }
    public string State { get; set; } = "queued";
    public int Attempts { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public DateTimeOffset? NextAttemptAtUtc { get; set; }
    public Guid? LastCompletedLeaseId { get; set; }
    public string? LastOutcome { get; set; }
    public string? LastFailureCode { get; set; }
    public int? LastSmtpStatusCode { get; set; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

/// <summary>Retained even in email-disabled outputs so migrations and runtime inspection preserve queue history.</summary>
internal static class OrganizationEmailDeliveryMapping
{
    public static void Configure(ModelBuilder builder) => builder.Entity<OrganizationEmailDeliveryRecord>(entity =>
    {
        entity.ToTable("email_deliveries", "infrastructure", table =>
        {
            table.HasCheckConstraint("CK_organization_email_deliveries_state",
                "\"State\" IN ('queued', 'leased', 'delivered', 'suppressed', 'terminal')");
            table.HasCheckConstraint("CK_organization_email_deliveries_attempts", "\"Attempts\" BETWEEN 0 AND 5");
            table.HasCheckConstraint("CK_organization_email_deliveries_template_version", "\"TemplateVersion\" > 0");
            table.HasCheckConstraint("CK_organization_email_deliveries_lease",
                "\"State\" <> 'leased' OR (\"LeaseId\" IS NOT NULL AND \"LeaseUntilUtc\" IS NOT NULL)");
        });
        entity.HasKey(row => row.Id);
        entity.Property(row => row.AdmissionIdentityKey).HasMaxLength(120);
        entity.Property(row => row.TemplateKey).HasMaxLength(160);
        entity.Property(row => row.ProtectedIntent).HasMaxLength(262144);
        entity.Property(row => row.State).HasMaxLength(16);
        entity.Property(row => row.LastOutcome).HasMaxLength(48);
        entity.Property(row => row.LastFailureCode).HasMaxLength(64);
        entity.HasIndex(row => new { row.OrganizationId, row.AdmissionIdentityKey, row.TemplateKey, row.TemplateVersion, row.MessageId })
            .IsUnique();
        entity.HasIndex(row => new { row.State, row.NextAttemptAtUtc });
        entity.HasIndex(row => new { row.State, row.LeaseUntilUtc });
        entity.HasIndex(row => new { row.OrganizationId, row.CreatedAtUtc });
    });
}
