using Trykatch.Domain.Organizations;
using Trykatch.Application.Organizations;
using Trykatch.Modules;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Trykatch.Infrastructure.Persistence;

public sealed class ApplicationDbContext(
    DbContextOptions<ApplicationDbContext> options,
    IEnumerable<IApplicationModelContributor> modelContributors,
    IOrganizationContext? organizationContext = null,
    ModuleCatalog? moduleCatalog = null) : DbContext(options)
{
    private readonly IApplicationModelContributor[] contributors = modelContributors
        .OrderBy(contributor => contributor.ModuleId, StringComparer.Ordinal)
        .ToArray();

    internal string ModelCompositionKey { get; } = CreateModelCompositionKey(modelContributors, moduleCatalog);
    public bool HasOrganizationScope => organizationContext?.IsResolved == true;
    public Guid CurrentOrganizationId => HasOrganizationScope ? organizationContext!.OrganizationId : Guid.Empty;

    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : this(options, [])
    {
    }

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, ApplicationModelCacheKeyFactory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        string? duplicateModule = contributors
            .GroupBy(contributor => contributor.ModuleId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateModule is not null)
            throw new InvalidOperationException($"Duplicate application model contributor for module '{duplicateModule}'.");

        foreach (IApplicationModelContributor contributor in contributors)
            contributor.Configure(modelBuilder);

        modelBuilder.Entity<AuditEntry>(entity =>
        {
            entity.ToTable("audit_entries", "platform");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Action).HasMaxLength(120);
            entity.Property(x => x.SubjectType).HasMaxLength(120);
            entity.Property(x => x.SubjectId).HasMaxLength(160);
            entity.Property(x => x.SubjectDisplayName).HasMaxLength(240);
            entity.Property(x => x.Details).HasColumnType("jsonb");
            entity.HasIndex(x => new { x.OrganizationId, x.OccurredAt });
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("outbox_messages", "platform");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Type).HasMaxLength(500);
            entity.Property(x => x.Payload).HasColumnType("jsonb");
            entity.HasIndex(x => new { x.ProcessedAt, x.OccurredAt });
        });

        ApplyOrganizationIsolation(modelBuilder);
        ApplicationModelIsolationValidator.Validate(modelBuilder.Model, moduleCatalog?.Descriptors ?? []);
    }

    private void ApplyOrganizationIsolation(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(entity => typeof(IOrganizationOwned).IsAssignableFrom(entity.ClrType)))
        {
            ParameterExpression row = Expression.Parameter(entityType.ClrType, "row");
            Expression organizationId = Expression.Property(row, nameof(IOrganizationOwned.OrganizationId));
            Expression hasScope = Expression.Property(Expression.Constant(this), nameof(HasOrganizationScope));
            Expression currentOrganizationId = Expression.Property(Expression.Constant(this), nameof(CurrentOrganizationId));
            LambdaExpression filter = Expression.Lambda(
                Expression.AndAlso(hasScope, Expression.Equal(organizationId, currentOrganizationId)),
                row);
            modelBuilder.Entity(entityType.ClrType).HasQueryFilter(
                ApplicationModelIsolationValidator.OrganizationIsolationFilter,
                filter);
        }
    }

    private static string CreateModelCompositionKey(
        IEnumerable<IApplicationModelContributor> modelContributors,
        ModuleCatalog? moduleCatalog)
    {
        string composition = JsonSerializer.Serialize(new
        {
            Contributors = modelContributors
                .OrderBy(contributor => contributor.ModuleId, StringComparer.Ordinal)
                .Select(contributor => new
                {
                    contributor.ModuleId,
                    Type = contributor.GetType().AssemblyQualifiedName
                }),
            Descriptors = moduleCatalog?.Descriptors
                .OrderBy(descriptor => descriptor.Id, StringComparer.Ordinal)
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(composition)));
    }
}
