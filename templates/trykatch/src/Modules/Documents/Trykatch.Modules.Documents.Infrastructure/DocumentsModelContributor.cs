using Microsoft.EntityFrameworkCore;
using Trykatch.Modules;
using Trykatch.Modules.Documents.Domain;

namespace Trykatch.Modules.Documents.Infrastructure;

public sealed class DocumentsModelContributor : IApplicationModelContributor
{
    public string ModuleId => "documents";

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DocumentRecord>(entity =>
        {
            entity.ToTable("documents", "app", table => table.ExcludeFromMigrations());
            entity.HasKey(document => document.Id);
            entity.Property(document => document.Title).HasMaxLength(200);
            entity.Property(document => document.DeletionReason).HasMaxLength(500);
            entity.Ignore(document => document.LifecycleState);
            entity.HasIndex(document => new { document.OrganizationId, document.CreatedAt });
            entity.HasQueryFilter("LifecycleVisibility", document => document.ArchivedAt == null && document.DeletedAt == null);
        });
    }
}
