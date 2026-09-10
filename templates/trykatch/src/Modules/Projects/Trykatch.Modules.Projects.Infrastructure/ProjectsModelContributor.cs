using Trykatch.Modules.Projects.Domain;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;
using Microsoft.EntityFrameworkCore;

namespace Trykatch.Modules.Projects.Infrastructure;

public sealed class ProjectsModelContributor : IApplicationModelContributor
{
    public string ModuleId => "projects";

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Project>(entity =>
        {
            entity.ToTable("projects", "app");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(120);
            entity.Property(x => x.Description).HasMaxLength(2000);
            entity.HasIndex(x => new { x.OrganizationId, x.Name });
            entity.Property(x => x.DeletionReason).HasMaxLength(500);
            entity.HasQueryFilter("LifecycleVisibility", x => x.ArchivedAt == null && x.DeletedAt == null);
        });
    }
}
