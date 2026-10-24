using Microsoft.EntityFrameworkCore;

namespace Trykatch.Identity;

internal sealed class IdentityEmailDeliveryDbContext(DbContextOptions<IdentityEmailDeliveryDbContext> options)
    : DbContext(options)
{
    internal DbSet<IdentityEmailDeliveryRecord> EmailDeliveries => Set<IdentityEmailDeliveryRecord>();

    protected override void OnModelCreating(ModelBuilder builder) => IdentityEmailDeliveryMapping.Configure(builder);
}
