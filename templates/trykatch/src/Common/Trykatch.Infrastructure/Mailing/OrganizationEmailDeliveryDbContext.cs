using Microsoft.EntityFrameworkCore;

namespace Trykatch.Infrastructure.Mailing;

internal sealed class OrganizationEmailDeliveryDbContext(
    DbContextOptions<OrganizationEmailDeliveryDbContext> options) : DbContext(options)
{
    internal DbSet<OrganizationEmailDeliveryRecord> EmailDeliveries => Set<OrganizationEmailDeliveryRecord>();

    protected override void OnModelCreating(ModelBuilder builder) => OrganizationEmailDeliveryMapping.Configure(builder);
}
