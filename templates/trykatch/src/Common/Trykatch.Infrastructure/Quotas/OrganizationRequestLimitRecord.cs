using Microsoft.EntityFrameworkCore;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Quotas;

internal sealed class OrganizationRequestLimitRecord : IOrganizationOwned
{
    public Guid OrganizationId { get; set; }
    public string Policy { get; set; } = string.Empty;
    public DateTimeOffset WindowStartUtc { get; set; }
    public int Attempts { get; set; }

    internal static void Configure(ModelBuilder model) => model.Entity<OrganizationRequestLimitRecord>(entity =>
    {
        entity.ToTable("organization_request_limits", "app", table =>
        {
            table.HasCheckConstraint("CK_organization_request_limits_identity",
                "\"OrganizationId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"Policy\" = 'organization-api'");
            table.HasCheckConstraint("CK_organization_request_limits_window", "isfinite(\"WindowStartUtc\") AND \"Attempts\" BETWEEN 1 AND 1000000");
        });
        entity.HasKey(row => new { row.OrganizationId, row.Policy });
        entity.Property(row => row.Policy).HasMaxLength(32);
    });
}
