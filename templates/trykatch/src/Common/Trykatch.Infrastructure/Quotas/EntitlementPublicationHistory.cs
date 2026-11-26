using Microsoft.EntityFrameworkCore;
using Trykatch.Domain.Organizations;

namespace Trykatch.Infrastructure.Quotas;

internal sealed class EntitlementPublicationHistory
{
    public Guid OrganizationId { get; set; }
    public long SourceRevision { get; set; }
    public string SourceHash { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public int SourceVersion { get; set; }
    public Guid ActorId { get; set; }
    public int InitialResourceCount { get; set; }
    public DateTimeOffset PublishedAtUtc { get; set; }
}

internal static class EntitlementPublicationHistoryMapping
{
    private const string Nonempty = "'00000000-0000-0000-0000-000000000000'::uuid";

    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EntitlementPublicationHistory>(entity =>
        {
            entity.ToTable("entitlement_publication_history", "platform", table =>
            {
                table.HasCheckConstraint("CK_entitlement_publication_history_identity", "\"OrganizationId\" <> " + Nonempty + "\n"
                    + "AND \"SourceRevision\" >= 0 AND \"ActorId\" <> " + Nonempty + "\n"
                    + "AND \"InitialResourceCount\" BETWEEN 0 AND 10000 AND isfinite(\"PublishedAtUtc\")");
                table.HasCheckConstraint("CK_entitlement_publication_history_source", "\"SourceHash\" ~ '^[A-Fa-f0-9]{64}$'\n"
                    + "AND \"Source\" = 'host-configuration' AND \"SourceVersion\" = 1");
            });
            entity.HasKey(row => new { row.OrganizationId, row.SourceRevision });
            entity.Property(row => row.SourceHash).HasMaxLength(64);
            entity.Property(row => row.Source).HasMaxLength(80);
            entity.HasOne<Organization>().WithMany().HasForeignKey(row => row.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
