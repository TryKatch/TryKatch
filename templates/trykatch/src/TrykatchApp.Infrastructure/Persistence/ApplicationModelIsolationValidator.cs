using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TrykatchApp.Modules;

namespace TrykatchApp.Infrastructure.Persistence;

/// <summary>
/// Fails model construction when a module can persist an undeclared or
/// incompletely isolated application relation. PostgreSQL inspection performs the
/// corresponding post-migration validation against the physical database.
/// </summary>
public static class ApplicationModelIsolationValidator
{
    public const string OrganizationIsolationFilter = "OrganizationIsolation";

    private static readonly TrykatchDataResourceDescriptor[] HostResources =
    [
        new(
            "audit-entries",
            "platform",
            "audit_entries",
            TrykatchDataOwnership.Organization,
            typeof(global::TrykatchApp.Domain.Organizations.AuditEntry).FullName,
            "audit_organization_isolation"),
        new(
            "outbox-messages",
            "platform",
            "outbox_messages",
            TrykatchDataOwnership.Infrastructure,
            typeof(OutboxMessage).FullName,
            AccessRule: TrykatchDataAccessRule.OutboxAppendOnly)
    ];

    public static void Validate(IReadOnlyModel model, IReadOnlyList<TrykatchModuleDescriptor> modules)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(modules);

        foreach (TrykatchDataResourceDescriptor resource in modules.SelectMany(module => module.DataResources))
            TrykatchDataResourceRules.Validate(resource);
        foreach (TrykatchDataResourceDescriptor resource in HostResources)
            TrykatchDataResourceRules.Validate(resource, hostOwned: true);

        TrykatchDataResourceDescriptor[] declared = HostResources
            .Concat(modules.SelectMany(module => module.DataResources))
            .ToArray();
        string? duplicateRelation = declared
            .GroupBy(resource => Relation(resource.Schema, resource.Table), StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateRelation is not null)
            throw new InvalidOperationException($"Persistent relation '{duplicateRelation}' is declared more than once.");

        Dictionary<string, TrykatchDataResourceDescriptor> byRelation = declared.ToDictionary(
            resource => Relation(resource.Schema, resource.Table),
            StringComparer.Ordinal);
        Dictionary<string, IReadOnlyEntityType> mapped = model.GetEntityTypes()
            .Where(entity => entity.GetTableName() is not null)
            .ToDictionary(
                entity => Relation(entity.GetSchema() ?? "public", entity.GetTableName()!),
                StringComparer.Ordinal);

        foreach ((string relation, IReadOnlyEntityType entity) in mapped)
        {
            if (!byRelation.TryGetValue(relation, out TrykatchDataResourceDescriptor? resource))
                throw new InvalidOperationException(
                    $"Persistent entity '{entity.ClrType.FullName}' maps undeclared relation '{relation}'.");
            ValidateEntity(entity, resource);
        }

        foreach (TrykatchDataResourceDescriptor resource in declared.Where(resource => resource.EntityType is not null))
        {
            string relation = Relation(resource.Schema, resource.Table);
            if (!mapped.ContainsKey(relation))
                throw new InvalidOperationException(
                    $"Declared persistent resource '{resource.Name}' is absent from the application model at '{relation}'.");
        }
    }

    private static void ValidateEntity(IReadOnlyEntityType entity, TrykatchDataResourceDescriptor resource)
    {
        if (resource.EntityType is not null
            && !string.Equals(resource.EntityType, entity.ClrType.FullName, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' declares entity '{resource.EntityType}' but '{entity.ClrType.FullName}' maps '{resource.Schema}.{resource.Table}'.");

        bool implementsOwnership = typeof(IOrganizationOwned).IsAssignableFrom(entity.ClrType);
        if (resource.Ownership != TrykatchDataOwnership.Organization)
        {
            if (implementsOwnership)
                throw new InvalidOperationException(
                    $"Entity '{entity.ClrType.FullName}' implements IOrganizationOwned but resource '{resource.Name}' is classified '{resource.Ownership}'.");
            return;
        }

        if (!implementsOwnership)
            throw new InvalidOperationException(
                $"Organization resource '{resource.Name}' entity '{entity.ClrType.FullName}' must implement IOrganizationOwned.");
        IReadOnlyProperty? organizationId = entity.FindProperty(nameof(IOrganizationOwned.OrganizationId));
        if (organizationId is null || organizationId.ClrType != typeof(Guid) || organizationId.IsNullable)
            throw new InvalidOperationException(
                $"Organization resource '{resource.Name}' must have a non-null Guid OrganizationId property.");
        if (entity.FindDeclaredQueryFilter(OrganizationIsolationFilter) is null)
            throw new InvalidOperationException(
                $"Organization resource '{resource.Name}' is missing the named '{OrganizationIsolationFilter}' query filter.");
        if (!entity.GetIndexes().Any(index =>
                index.Properties.Count > 0
                && string.Equals(index.Properties[0].Name, nameof(IOrganizationOwned.OrganizationId), StringComparison.Ordinal)))
            throw new InvalidOperationException(
                $"Organization resource '{resource.Name}' requires an index beginning with OrganizationId.");
    }

    private static string Relation(string schema, string table) => $"{schema}.{table}";
}
