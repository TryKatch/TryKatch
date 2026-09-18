using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Trykatch.Infrastructure.Persistence;

/// <summary>Scaffolds the platform model without starting the API, identity or provider services.</summary>
public sealed class PlatformDbContextDesignFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args)
    {
        string connection = Environment.GetEnvironmentVariable("TRYKATCH_DESIGN_CONNECTION")
            ?? "Host=localhost;Database=design_only;Username=design_only;Password=design_only";
        return new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(connection).Options);
    }
}
