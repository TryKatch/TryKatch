using Microsoft.EntityFrameworkCore;
using __ROOT_NAMESPACE__.Modules;
using __ROOT_NAMESPACE__.Modules.__MODULE__.Domain;

namespace __ROOT_NAMESPACE__.Modules.__MODULE__.Infrastructure;

public sealed class __MODULE__ModelContributor : IApplicationModelContributor
{
    public string ModuleId => "__MODULE_ID__";

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<__ENTITY__Record>(entity =>
        {
            entity.ToTable("__RESOURCE__", "app", table => table.ExcludeFromMigrations());
            entity.HasKey(record => record.Id);
            __MODEL_FIELD_CONFIGURATION__
            entity.Property(record => record.DeletionReason).HasMaxLength(500);
            entity.Ignore(record => record.LifecycleState);
            entity.HasIndex(record => new { record.OrganizationId, record.CreatedAt });
            entity.HasQueryFilter("LifecycleVisibility", record => record.ArchivedAt == null && record.DeletedAt == null);
        });
    }
}
