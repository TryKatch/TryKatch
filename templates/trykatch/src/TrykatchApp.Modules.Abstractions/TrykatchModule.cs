using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace TrykatchApp.Modules;

[Flags]
public enum TrykatchModuleCapabilities
{
    None = 0,
    Api = 1,
    Web = 2,
    Data = 4,
    BackgroundWork = 8,
    Assistant = 16
}

public enum TrykatchExtensionPointKind
{
    UiSlot,
    DataTable,
    Form,
    Component,
    Api,
    Event
}

public sealed record TrykatchExtensionPointDescriptor(
    string Id,
    string Description,
    TrykatchExtensionPointKind Kind,
    TrykatchModuleCapabilities Surface);

public enum TrykatchAssistantToolRisk
{
    ReadOnly,
    Mutating,
    Destructive
}

/// <summary>The host-enforced ownership class for one persistent module resource.</summary>
public enum TrykatchDataOwnership
{
    Organization,
    Platform,
    Global,
    Infrastructure
}

/// <summary>
/// Declares one persistent relation owned by a module. Schema and table names are
/// provider identifiers, not arbitrary SQL, and are inspected after migration.
/// </summary>
public sealed record TrykatchDataResourceDescriptor(
    string Name,
    string Schema,
    string Table,
    TrykatchDataOwnership Ownership,
    string? EntityType = null,
    string? IsolationPolicy = null,
    TrykatchDataAccessRule? AccessRule = null);

public enum TrykatchDataAccessRule
{
    PlatformOnly,
    IdentityOnly,
    GlobalReadOnly,
    HostOnly,
    OutboxAppendOnly
}

/// <summary>Declarative installed schema metadata; it never activates module code.</summary>
public sealed record TrykatchInstalledDataResource(string ModuleId, TrykatchDataResourceDescriptor Resource);

public sealed record TrykatchPermissionDescriptor(
    string Key,
    string Name,
    string Description,
    bool IsSensitive = false,
    int Order = 0,
    IReadOnlyList<string>? DefaultRoles = null);

/// <summary>
/// Contract implemented by every EF entity whose rows belong to an organization.
/// The host uses it to install an automatic query filter and validate the model.
/// </summary>
public interface IOrganizationOwned
{
    Guid OrganizationId { get; }
}

/// <summary>Module-owned EF mapping seam composed by the host application context.</summary>
public interface IApplicationModelContributor
{
    string ModuleId { get; }
    void Configure(ModelBuilder modelBuilder);
}

/// <summary>
/// Organization-scoped data interface supplied by the host. Query filters,
/// connection routing, transactions, and RLS remain host-owned.
/// </summary>
public interface IOrganizationModuleData
{
    Guid OrganizationId { get; }
    Guid ActorId { get; }
    IQueryable<TEntity> Query<TEntity>() where TEntity : class;
    void Add<TEntity>(TEntity entity) where TEntity : class;
    void Remove<TEntity>(TEntity entity) where TEntity : class;
    void RecordAudit(
        string action,
        string subjectType,
        string subjectId,
        string displayName,
        IReadOnlyDictionary<string, string?>? details = null);
    void Enqueue<TMessage>(TMessage message) where TMessage : notnull;
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Host-owned authorization seam for module application use cases. HTTP policy
/// metadata remains the first check; mutating use cases repeat it here.
/// </summary>
public interface IModulePermissionAuthorizer
{
    Task<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken = default);
}

/// <summary>
/// An explicit, provider-neutral allowlist entry for exposing one API operation
/// to an AI assistant. Authorization remains enforced by the API operation.
/// </summary>
public sealed record TrykatchAssistantToolDescriptor(
    string Name,
    string OperationId,
    string Description,
    TrykatchAssistantToolRisk Risk,
    bool RequiresHumanConfirmation);

public sealed record TrykatchModuleDescriptor(
    string Id,
    string Name,
    string Version,
    string Description,
    IReadOnlyList<string> Requires,
    IReadOnlyList<string> OptionalDependencies,
    TrykatchModuleCapabilities Capabilities,
    IReadOnlyList<TrykatchExtensionPointDescriptor> ExtensionPoints)
{
    public IReadOnlyList<TrykatchAssistantToolDescriptor> AssistantTools { get; init; } = [];
    public TrykatchDataOwnership? DefaultDataOwnership { get; init; }
    public IReadOnlyList<TrykatchDataResourceDescriptor> DataResources { get; init; } = [];
    public string Publisher { get; init; } = "trykatch";
    public IReadOnlyList<TrykatchPermissionDescriptor> Permissions { get; init; } = [];
}

/// <summary>
/// The stable install-time seam for a Trykatch module. Implementations own their
/// registration complexity; the host owns ordering, validation, and activation.
/// </summary>
public interface ITrykatchModule
{
    TrykatchModuleDescriptor Descriptor { get; }
    void Register(IServiceCollection services, IConfiguration configuration);
}

/// <summary>Associates a host-discovered type with the module that owns its activation.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class TrykatchModuleAttribute(string moduleId) : Attribute
{
    public string ModuleId { get; } = moduleId;
}

public sealed class TrykatchModuleCatalog
{
    private static readonly Regex StableId = new(
        "^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex StableVersion = new(
        "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex StableContractId = new(
        "^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex StableToolName = new(
        "^[a-z][a-z0-9]*(?:_[a-z0-9]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex StableOperationId = new(
        "^[A-Za-z][A-Za-z0-9_]*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public TrykatchModuleCatalog(IEnumerable<ITrykatchModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ITrykatchModule[] supplied = modules.ToArray();
        ValidateDescriptors(supplied);

        Dictionary<string, ITrykatchModule> byId = supplied.ToDictionary(
            module => module.Descriptor.Id,
            StringComparer.Ordinal);
        ValidateDependencies(byId);

        Modules = OrderByDependencies(byId);
        Descriptors = Modules.Select(module => module.Descriptor).ToArray();
        ModuleIds = Descriptors.Select(descriptor => descriptor.Id).ToFrozenSet(StringComparer.Ordinal);
    }

    public IReadOnlyList<ITrykatchModule> Modules { get; }
    public IReadOnlyList<TrykatchModuleDescriptor> Descriptors { get; }
    public IReadOnlySet<string> ModuleIds { get; }

    public bool Contains(string moduleId) => ModuleIds.Contains(moduleId);

    private static void ValidateDescriptors(IReadOnlyCollection<ITrykatchModule> modules)
    {
        string? duplicate = modules
            .GroupBy(module => module.Descriptor.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
            throw new InvalidOperationException($"Duplicate Trykatch module id '{duplicate}'.");

        foreach (ITrykatchModule module in modules)
        {
            TrykatchModuleDescriptor descriptor = module.Descriptor;
            if (descriptor.Id.Length > 80 || !StableId.IsMatch(descriptor.Id))
                throw new InvalidOperationException($"Trykatch module id '{descriptor.Id}' must be lower-case kebab-case and no longer than 80 characters.");
            if (string.IsNullOrWhiteSpace(descriptor.Name) || string.IsNullOrWhiteSpace(descriptor.Description))
                throw new InvalidOperationException($"Trykatch module '{descriptor.Id}' requires a name and description.");
            if (!StableVersion.IsMatch(descriptor.Version))
                throw new InvalidOperationException($"Trykatch module '{descriptor.Id}' has invalid semantic version '{descriptor.Version}'.");

            ValidateDataOwnership(descriptor);
            EnsureUnique(descriptor.Permissions.Select(permission => permission.Key), descriptor.Id, "permission");
            foreach (TrykatchPermissionDescriptor permission in descriptor.Permissions)
            {
                if (!StableContractId.IsMatch(permission.Key)
                    || string.IsNullOrWhiteSpace(permission.Name)
                    || string.IsNullOrWhiteSpace(permission.Description))
                    throw new InvalidOperationException($"Trykatch module '{descriptor.Id}' declares invalid permission '{permission.Key}'.");
            }

            EnsureUnique(descriptor.Requires, descriptor.Id, "required dependency");
            EnsureUnique(descriptor.OptionalDependencies, descriptor.Id, "optional dependency");
            foreach (string dependency in descriptor.Requires.Concat(descriptor.OptionalDependencies))
            {
                if (!StableId.IsMatch(dependency))
                    throw new InvalidOperationException($"Trykatch module '{descriptor.Id}' declares invalid dependency id '{dependency}'.");
            }
            string? overlap = descriptor.Requires.Intersect(descriptor.OptionalDependencies, StringComparer.Ordinal).FirstOrDefault();
            if (overlap is not null)
                throw new InvalidOperationException($"Trykatch module '{descriptor.Id}' declares '{overlap}' as both required and optional.");
            if (descriptor.Requires.Contains(descriptor.Id, StringComparer.Ordinal)
                || descriptor.OptionalDependencies.Contains(descriptor.Id, StringComparer.Ordinal))
                throw new InvalidOperationException($"Trykatch module '{descriptor.Id}' cannot depend on itself.");

            EnsureUnique(descriptor.ExtensionPoints.Select(point => point.Id), descriptor.Id, "extension point");
            foreach (TrykatchExtensionPointDescriptor point in descriptor.ExtensionPoints)
            {
                if (point.Id.Length > 120 || !StableContractId.IsMatch(point.Id))
                    throw new InvalidOperationException($"Trykatch module '{descriptor.Id}' declares invalid extension point id '{point.Id}'.");
                if (string.IsNullOrWhiteSpace(point.Description))
                    throw new InvalidOperationException($"Trykatch extension point '{point.Id}' requires a description.");
                if (point.Surface == TrykatchModuleCapabilities.None
                    || (descriptor.Capabilities & point.Surface) != point.Surface)
                    throw new InvalidOperationException($"Trykatch extension point '{point.Id}' uses a surface not provided by module '{descriptor.Id}'.");
            }

            EnsureUnique(descriptor.AssistantTools.Select(tool => tool.Name), descriptor.Id, "assistant tool name");
            EnsureUnique(descriptor.AssistantTools.Select(tool => tool.OperationId), descriptor.Id, "assistant tool operation");
            if (descriptor.AssistantTools.Count > 0
                && !descriptor.Capabilities.HasFlag(TrykatchModuleCapabilities.Assistant))
                throw new InvalidOperationException($"Trykatch module '{descriptor.Id}' declares assistant tools without the Assistant capability.");
            foreach (TrykatchAssistantToolDescriptor tool in descriptor.AssistantTools)
            {
                if (tool.Name.Length > 64 || !StableToolName.IsMatch(tool.Name))
                    throw new InvalidOperationException($"Trykatch assistant tool '{tool.Name}' must be lower-case snake_case and no longer than 64 characters.");
                if (!StableOperationId.IsMatch(tool.OperationId))
                    throw new InvalidOperationException($"Trykatch assistant tool '{tool.Name}' has invalid operation id '{tool.OperationId}'.");
                if (string.IsNullOrWhiteSpace(tool.Description))
                    throw new InvalidOperationException($"Trykatch assistant tool '{tool.Name}' requires a description.");
                if (tool.Risk != TrykatchAssistantToolRisk.ReadOnly && !tool.RequiresHumanConfirmation)
                    throw new InvalidOperationException($"Trykatch assistant tool '{tool.Name}' must require human confirmation because it can change state.");
            }
        }

        string? duplicatePoint = modules
            .SelectMany(module => module.Descriptor.ExtensionPoints)
            .GroupBy(point => point.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicatePoint is not null)
            throw new InvalidOperationException($"Duplicate Trykatch extension point id '{duplicatePoint}'.");

        string? duplicateTool = modules
            .SelectMany(module => module.Descriptor.AssistantTools)
            .GroupBy(tool => tool.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateTool is not null)
            throw new InvalidOperationException($"Duplicate Trykatch assistant tool name '{duplicateTool}'.");

        string? duplicateOperation = modules
            .SelectMany(module => module.Descriptor.AssistantTools)
            .GroupBy(tool => tool.OperationId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateOperation is not null)
            throw new InvalidOperationException($"Multiple Trykatch assistant tools target operation '{duplicateOperation}'.");
    }

    private static void ValidateDataOwnership(TrykatchModuleDescriptor descriptor)
    {
        bool hasData = descriptor.Capabilities.HasFlag(TrykatchModuleCapabilities.Data);
        if (hasData && descriptor.DefaultDataOwnership is null)
            throw new InvalidOperationException($"Trykatch data module '{descriptor.Id}' must declare exactly one default data ownership class.");
        if (!hasData && (descriptor.DefaultDataOwnership is not null || descriptor.DataResources.Count > 0))
            throw new InvalidOperationException($"Trykatch module '{descriptor.Id}' declares data ownership without the Data capability.");
        if (hasData && descriptor.DataResources.Count == 0)
            throw new InvalidOperationException($"Trykatch data module '{descriptor.Id}' must declare every persistent resource.");

        EnsureUnique(descriptor.DataResources.Select(resource => resource.Name), descriptor.Id, "data resource");
        EnsureUnique(
            descriptor.DataResources.Select(resource => $"{resource.Schema}.{resource.Table}"),
            descriptor.Id,
            "data relation");
        foreach (TrykatchDataResourceDescriptor resource in descriptor.DataResources)
        {
            TrykatchDataResourceRules.Validate(resource);
            if (!StableContractId.IsMatch(resource.Name)
                || !StableId.IsMatch(resource.Schema)
                || !StableId.IsMatch(resource.Table.Replace('_', '-')))
                throw new InvalidOperationException(
                    $"Trykatch module '{descriptor.Id}' declares invalid data resource '{resource.Name}' at '{resource.Schema}.{resource.Table}'.");
            if (resource.Ownership == TrykatchDataOwnership.Organization
                && string.IsNullOrWhiteSpace(resource.IsolationPolicy))
                throw new InvalidOperationException(
                    $"Trykatch organization resource '{descriptor.Id}/{resource.Name}' must declare its PostgreSQL isolation policy.");
        }
    }

    private static void ValidateDependencies(IReadOnlyDictionary<string, ITrykatchModule> modules)
    {
        foreach (ITrykatchModule module in modules.Values)
        {
            foreach (string dependency in module.Descriptor.Requires)
            {
                if (!modules.ContainsKey(dependency))
                    throw new InvalidOperationException($"Trykatch module '{module.Descriptor.Id}' requires missing module '{dependency}'.");
            }
        }
    }

    private static List<ITrykatchModule> OrderByDependencies(IReadOnlyDictionary<string, ITrykatchModule> modules)
    {
        List<ITrykatchModule> ordered = [];
        HashSet<string> visiting = new(StringComparer.Ordinal);
        HashSet<string> visited = new(StringComparer.Ordinal);

        foreach (string moduleId in modules.Keys.Order(StringComparer.Ordinal))
            Visit(moduleId);

        return ordered;

        void Visit(string moduleId)
        {
            if (visited.Contains(moduleId)) return;
            if (!visiting.Add(moduleId))
                throw new InvalidOperationException($"Trykatch module dependency cycle includes '{moduleId}'.");

            ITrykatchModule module = modules[moduleId];
            IEnumerable<string> dependencies = module.Descriptor.Requires.Concat(
                module.Descriptor.OptionalDependencies.Where(modules.ContainsKey));
            foreach (string dependency in dependencies.Order(StringComparer.Ordinal))
                Visit(dependency);

            visiting.Remove(moduleId);
            visited.Add(moduleId);
            ordered.Add(module);
        }
    }

    private static void EnsureUnique(IEnumerable<string> values, string moduleId, string subject)
    {
        string? duplicate = values
            .GroupBy(value => value, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
            throw new InvalidOperationException($"Trykatch module '{moduleId}' declares duplicate {subject} '{duplicate}'.");
    }
}

public static class TrykatchModuleServiceCollectionExtensions
{
    public static TrykatchModuleCatalog AddTrykatchModules(
        this IServiceCollection services,
        IConfiguration configuration,
        IEnumerable<ITrykatchModule> modules)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        TrykatchModuleCatalog catalog = new(modules);
        services.AddSingleton(catalog);
        foreach (ITrykatchModule module in catalog.Modules)
            module.Register(services, configuration);

        return catalog;
    }
}
