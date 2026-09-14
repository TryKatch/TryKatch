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
            entity.Property(document => document.Description).HasColumnName("Content").HasMaxLength(2_000);
            entity.Property(document => document.FileName).HasMaxLength(255);
            entity.Property(document => document.MediaType).HasMaxLength(127);
            entity.Property(document => document.Sha256).HasMaxLength(64).IsFixedLength();
            entity.Property(document => document.ObjectKey).HasMaxLength(500);
            entity.Property(document => document.DeletionReason).HasMaxLength(500);
            entity.Ignore(document => document.LifecycleState);
            entity.HasIndex(document => new { document.OrganizationId, document.CreatedAt });
            entity.HasIndex(document => new { document.OrganizationId, document.ObjectKey }).IsUnique();
            entity.HasQueryFilter("LifecycleVisibility", document => document.ArchivedAt == null && document.DeletedAt == null);
        });
    }
}
