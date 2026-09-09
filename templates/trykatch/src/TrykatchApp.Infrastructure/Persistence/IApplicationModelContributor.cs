using Microsoft.EntityFrameworkCore;

namespace TrykatchApp.Infrastructure.Persistence;

/// <summary>
/// Module-owned EF Core mapping seam for the shared application database.
/// Contributors are explicitly registered by enabled modules; no assembly
/// scanning is used. Historical migrations remain durable when a module is disabled.
/// </summary>
public interface IApplicationModelContributor
{
    string ModuleId { get; }
    void Configure(ModelBuilder modelBuilder);
}
