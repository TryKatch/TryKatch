using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FlatpackApp.Modules;

[Flags]
public enum FlatpackModuleCapabilities
{
    None = 0,
    Api = 1,
    Web = 2,
    Data = 4,
    BackgroundWork = 8,
    Assistant = 16
}

public enum FlatpackExtensionPointKind
{
    UiSlot,
    DataTable,
    Form,
    Component,
    Api,
    Event
}

public sealed record FlatpackExtensionPointDescriptor(
    string Id,
    string Description,
    FlatpackExtensionPointKind Kind,
    FlatpackModuleCapabilities Surface);

public sealed record FlatpackModuleDescriptor(
    string Id,
    string Name,
    string Version,
    string Description,
    IReadOnlyList<string> Requires,
    IReadOnlyList<string> OptionalDependencies,
    FlatpackModuleCapabilities Capabilities,
    IReadOnlyList<FlatpackExtensionPointDescriptor> ExtensionPoints);

/// <summary>
/// The stable install-time seam for a Flatpack module. Implementations own their
/// registration complexity; the host owns ordering, validation, and activation.
/// </summary>
public interface IFlatpackModule
{
    FlatpackModuleDescriptor Descriptor { get; }
    void Register(IServiceCollection services, IConfiguration configuration);
}

/// <summary>Associates a host-discovered type with the module that owns its activation.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class FlatpackModuleAttribute(string moduleId) : Attribute
{
    public string ModuleId { get; } = moduleId;
}

public sealed class FlatpackModuleCatalog
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

    public FlatpackModuleCatalog(IEnumerable<IFlatpackModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        IFlatpackModule[] supplied = modules.ToArray();
        ValidateDescriptors(supplied);

        Dictionary<string, IFlatpackModule> byId = supplied.ToDictionary(
            module => module.Descriptor.Id,
            StringComparer.Ordinal);
        ValidateDependencies(byId);

        Modules = OrderByDependencies(byId);
        Descriptors = Modules.Select(module => module.Descriptor).ToArray();
        ModuleIds = Descriptors.Select(descriptor => descriptor.Id).ToFrozenSet(StringComparer.Ordinal);
    }

    public IReadOnlyList<IFlatpackModule> Modules { get; }
    public IReadOnlyList<FlatpackModuleDescriptor> Descriptors { get; }
    public IReadOnlySet<string> ModuleIds { get; }

    public bool Contains(string moduleId) => ModuleIds.Contains(moduleId);

    private static void ValidateDescriptors(IReadOnlyCollection<IFlatpackModule> modules)
    {
        string? duplicate = modules
            .GroupBy(module => module.Descriptor.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
            throw new InvalidOperationException($"Duplicate Flatpack module id '{duplicate}'.");

        foreach (IFlatpackModule module in modules)
        {
            FlatpackModuleDescriptor descriptor = module.Descriptor;
            if (descriptor.Id.Length > 80 || !StableId.IsMatch(descriptor.Id))
                throw new InvalidOperationException($"Flatpack module id '{descriptor.Id}' must be lower-case kebab-case and no longer than 80 characters.");
            if (string.IsNullOrWhiteSpace(descriptor.Name) || string.IsNullOrWhiteSpace(descriptor.Description))
                throw new InvalidOperationException($"Flatpack module '{descriptor.Id}' requires a name and description.");
            if (!StableVersion.IsMatch(descriptor.Version))
                throw new InvalidOperationException($"Flatpack module '{descriptor.Id}' has invalid semantic version '{descriptor.Version}'.");

            EnsureUnique(descriptor.Requires, descriptor.Id, "required dependency");
            EnsureUnique(descriptor.OptionalDependencies, descriptor.Id, "optional dependency");
            foreach (string dependency in descriptor.Requires.Concat(descriptor.OptionalDependencies))
            {
                if (!StableId.IsMatch(dependency))
                    throw new InvalidOperationException($"Flatpack module '{descriptor.Id}' declares invalid dependency id '{dependency}'.");
            }
            string? overlap = descriptor.Requires.Intersect(descriptor.OptionalDependencies, StringComparer.Ordinal).FirstOrDefault();
            if (overlap is not null)
                throw new InvalidOperationException($"Flatpack module '{descriptor.Id}' declares '{overlap}' as both required and optional.");
            if (descriptor.Requires.Contains(descriptor.Id, StringComparer.Ordinal)
                || descriptor.OptionalDependencies.Contains(descriptor.Id, StringComparer.Ordinal))
                throw new InvalidOperationException($"Flatpack module '{descriptor.Id}' cannot depend on itself.");

            EnsureUnique(descriptor.ExtensionPoints.Select(point => point.Id), descriptor.Id, "extension point");
            foreach (FlatpackExtensionPointDescriptor point in descriptor.ExtensionPoints)
            {
                if (point.Id.Length > 120 || !StableContractId.IsMatch(point.Id))
                    throw new InvalidOperationException($"Flatpack module '{descriptor.Id}' declares invalid extension point id '{point.Id}'.");
                if (string.IsNullOrWhiteSpace(point.Description))
                    throw new InvalidOperationException($"Flatpack extension point '{point.Id}' requires a description.");
                if (point.Surface == FlatpackModuleCapabilities.None
                    || (descriptor.Capabilities & point.Surface) != point.Surface)
                    throw new InvalidOperationException($"Flatpack extension point '{point.Id}' uses a surface not provided by module '{descriptor.Id}'.");
            }
        }

        string? duplicatePoint = modules
            .SelectMany(module => module.Descriptor.ExtensionPoints)
            .GroupBy(point => point.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicatePoint is not null)
            throw new InvalidOperationException($"Duplicate Flatpack extension point id '{duplicatePoint}'.");
    }

    private static void ValidateDependencies(IReadOnlyDictionary<string, IFlatpackModule> modules)
    {
        foreach (IFlatpackModule module in modules.Values)
        {
            foreach (string dependency in module.Descriptor.Requires)
            {
                if (!modules.ContainsKey(dependency))
                    throw new InvalidOperationException($"Flatpack module '{module.Descriptor.Id}' requires missing module '{dependency}'.");
            }
        }
    }

    private static List<IFlatpackModule> OrderByDependencies(IReadOnlyDictionary<string, IFlatpackModule> modules)
    {
        List<IFlatpackModule> ordered = [];
        HashSet<string> visiting = new(StringComparer.Ordinal);
        HashSet<string> visited = new(StringComparer.Ordinal);

        foreach (string moduleId in modules.Keys.Order(StringComparer.Ordinal))
            Visit(moduleId);

        return ordered;

        void Visit(string moduleId)
        {
            if (visited.Contains(moduleId)) return;
            if (!visiting.Add(moduleId))
                throw new InvalidOperationException($"Flatpack module dependency cycle includes '{moduleId}'.");

            IFlatpackModule module = modules[moduleId];
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
            throw new InvalidOperationException($"Flatpack module '{moduleId}' declares duplicate {subject} '{duplicate}'.");
    }
}

public static class FlatpackModuleServiceCollectionExtensions
{
    public static FlatpackModuleCatalog AddFlatpackModules(
        this IServiceCollection services,
        IConfiguration configuration,
        IEnumerable<IFlatpackModule> modules)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        FlatpackModuleCatalog catalog = new(modules);
        services.AddSingleton(catalog);
        foreach (IFlatpackModule module in catalog.Modules)
            module.Register(services, configuration);

        return catalog;
    }
}
