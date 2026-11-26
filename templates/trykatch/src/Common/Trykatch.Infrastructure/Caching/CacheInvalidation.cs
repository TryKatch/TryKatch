using Microsoft.EntityFrameworkCore;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Caching;

/// <summary>Durable organization-scoped watermark; never reset to the initial cache generation.</summary>
internal sealed class CacheInvalidation : IOrganizationOwned
{
    public Guid OrganizationId { get; init; }
    public string KeyHash { get; init; } = string.Empty;
    public Guid Generation { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }

    internal static void Configure(ModelBuilder modelBuilder) => modelBuilder.Entity<CacheInvalidation>(entity =>
    {
        entity.ToTable("cache_invalidations", "app", table =>
        {
            table.HasCheckConstraint("CK_cache_invalidations_hash", "\"KeyHash\" ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint("CK_cache_invalidations_generation", "\"Generation\" <> '00000000-0000-0000-0000-000000000000'::uuid");
        });
        entity.HasKey(value => new { value.OrganizationId, value.KeyHash });
        entity.Property(value => value.KeyHash).HasMaxLength(64);
        entity.HasIndex(value => new { value.OrganizationId, value.UpdatedAtUtc });
    });
}
