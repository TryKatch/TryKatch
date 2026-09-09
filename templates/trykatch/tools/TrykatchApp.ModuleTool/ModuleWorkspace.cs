using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace TrykatchApp.ModuleTool;

public sealed partial class ModuleWorkspace
{
    private const int SupportedSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    private static readonly HashSet<string> KnownCapabilities = new(StringComparer.Ordinal)
    {
        "api", "web", "data", "background-work", "assistant"
    };

    private readonly string _root;
    private readonly string _catalogPath;
    private readonly IWorkspaceCommandRunner _commandRunner;

    public ModuleWorkspace(string root)
        : this(root, new ProcessWorkspaceCommandRunner())
    {
    }

    internal ModuleWorkspace(string root, IWorkspaceCommandRunner commandRunner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(commandRunner);
        _root = Path.GetFullPath(root);
        _catalogPath = ResolveInsideRoot("trykatch.modules.json");
        _commandRunner = commandRunner;
    }

    public ModuleDoctorReport Inspect()
    {
        List<string> errors = [];
        ModuleCatalogFile? catalog = ReadJson<ModuleCatalogFile>(_catalogPath, errors, "module catalog");
        if (catalog is null)
            return new([], errors);

        ValidateCatalog(catalog, errors);
        List<LoadedModule> modules = LoadModules(catalog, errors);
        ValidateModules(catalog, modules, errors);

        if (errors.Count == 0)
        {
            ValidateGeneratedRegistries(catalog, modules, errors);
            ValidateGeneratedLock(catalog, modules, errors);
        }

        return new(
            modules.Select(module => new ModuleStatus(
                module.Manifest.Id,
                module.Manifest.Name,
                module.Manifest.Version,
                module.Registration.Enabled,
                module.Registration.Manifest)).ToArray(),
            errors);
    }

    public ModuleDoctorReport Generate()
    {
        List<string> errors = [];
        ModuleCatalogFile? catalog = ReadJson<ModuleCatalogFile>(_catalogPath, errors, "module catalog");
        if (catalog is null)
            return new([], errors);

        ValidateCatalog(catalog, errors);
        List<LoadedModule> modules = LoadModules(catalog, errors);
        ValidateModules(catalog, modules, errors);
        if (errors.Count > 0)
            return ToReport(modules, errors);

        WriteGeneratedRegistries(catalog, modules);
        return ToReport(modules, []);
    }

    public ModuleDoctorReport SetEnabled(string moduleId, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        List<string> errors = [];
        ModuleCatalogFile? catalog = ReadJson<ModuleCatalogFile>(_catalogPath, errors, "module catalog");
        if (catalog is null)
            return new([], errors);

        ValidateCatalog(catalog, errors);
        if (errors.Count > 0)
            return new([], errors);

        ModuleRegistration? registration = catalog.Modules.SingleOrDefault(
            candidate => string.Equals(candidate.Id, moduleId, StringComparison.Ordinal));
        if (registration is null)
            return new([], [$"Unknown Trykatch module '{moduleId}'."]);

        registration.Enabled = enabled;
        List<LoadedModule> modules = LoadModules(catalog, errors);
        ValidateModules(catalog, modules, errors);
        if (errors.Count > 0)
            return ToReport(modules, errors);

        string catalogJson = JsonSerializer.Serialize(catalog, JsonOptions) + "\n";
        Dictionary<string, byte[]?> originals = CaptureFiles(catalog);
        try
        {
            WriteGeneratedRegistries(catalog, modules);
            WriteAtomic(_catalogPath, catalogJson);
        }
        catch
        {
            RestoreFiles(originals);
            throw;
        }

        return ToReport(modules, []);
    }

    private static ModuleDoctorReport ToReport(IEnumerable<LoadedModule> modules, IReadOnlyList<string> errors) =>
        new(
            modules.Select(module => new ModuleStatus(
                module.Manifest.Id,
                module.Manifest.Name,
                module.Manifest.Version,
                module.Registration.Enabled,
                module.Registration.Manifest)).ToArray(),
            errors);

    private void ValidateCatalog(ModuleCatalogFile catalog, List<string> errors)
    {
        if (catalog.SchemaVersion != SupportedSchemaVersion)
            errors.Add($"Unsupported module catalog schema version '{catalog.SchemaVersion}'. Expected '{SupportedSchemaVersion}'.");
        if (!TryParseVersion(catalog.HostVersion, out _))
            errors.Add($"Invalid Trykatch host version '{catalog.HostVersion}'.");
        if (catalog.Modules.Count == 0)
            errors.Add("The module catalog must contain at least one module.");
        EnsureUnique(catalog.Modules.Select(module => module.Id), "module registration id", errors);
        EnsureUnique(catalog.Modules.Select(module => module.Manifest), "module manifest path", errors);
        ValidateOutputPath(catalog.Outputs.Backend, "backend registry", errors);
        if (!DotnetNamespaceRegex().IsMatch(catalog.Outputs.BackendNamespace))
            errors.Add($"Invalid backend registry namespace '{catalog.Outputs.BackendNamespace}'.");
        if (!DotnetNamespaceRegex().IsMatch(catalog.Outputs.DotnetModuleContractNamespace))
            errors.Add($"Invalid .NET module contract namespace '{catalog.Outputs.DotnetModuleContractNamespace}'.");
        ValidateOutputPath(catalog.Outputs.Migrator, "migrator registry", errors);
        if (!DotnetNamespaceRegex().IsMatch(catalog.Outputs.MigratorNamespace))
            errors.Add($"Invalid migrator registry namespace '{catalog.Outputs.MigratorNamespace}'.");
        if (HasWebSurface())
        {
            ValidateOutputPath(catalog.Outputs.Web, "web registry", errors);
            if (string.IsNullOrWhiteSpace(catalog.Outputs.WebModuleSdkSpecifier))
                errors.Add("The web module SDK specifier must not be empty.");
        }
        ValidateOutputPath(catalog.LockFile, "module lock file", errors);

        foreach (ModuleRegistration module in catalog.Modules)
        {
            if (!StableIdRegex().IsMatch(module.Id))
                errors.Add($"Invalid module registration id '{module.Id}'.");
            ValidateRelativePath(module.Manifest, $"manifest for '{module.Id}'", errors);
        }
    }

    private List<LoadedModule> LoadModules(ModuleCatalogFile catalog, List<string> errors)
    {
        List<LoadedModule> loaded = [];
        foreach (ModuleRegistration registration in catalog.Modules)
        {
            string path;
            try
            {
                path = ResolveInsideRoot(registration.Manifest);
            }
            catch (InvalidOperationException exception)
            {
                errors.Add(exception.Message);
                continue;
            }

            ModuleManifest? manifest = ReadJson<ModuleManifest>(path, errors, $"manifest for '{registration.Id}'");
            if (manifest is not null)
            {
                bool isWorkspace = string.Equals(manifest.Distribution.Kind, "workspace", StringComparison.Ordinal);
                loaded.Add(new(
                    registration,
                    manifest,
                    isWorkspace ? ComputeWorkspaceManifestSha256(path, catalog, manifest) : ComputeFileSha256(path),
                    isWorkspace ? "template-normalized-sha256" : "sha256"));
            }
        }

        return loaded;
    }

    private void ValidateModules(ModuleCatalogFile catalog, IReadOnlyCollection<LoadedModule> modules, List<string> errors)
    {
        EnsureUnique(modules.Select(module => module.Manifest.Id), "manifest module id", errors);
        bool webEnabled = HasWebSurface();
        Version? hostVersion = TryParseVersion(catalog.HostVersion, out Version? parsedHost) ? parsedHost : null;

        foreach (LoadedModule module in modules)
        {
            ModuleManifest manifest = module.Manifest;
            if (manifest.SchemaVersion != SupportedSchemaVersion)
                errors.Add($"Module '{manifest.Id}' uses unsupported manifest schema version '{manifest.SchemaVersion}'.");
            if (!string.Equals(module.Registration.Id, manifest.Id, StringComparison.Ordinal))
                errors.Add($"Registration id '{module.Registration.Id}' does not match manifest id '{manifest.Id}'.");
            if (!StableIdRegex().IsMatch(manifest.Id))
                errors.Add($"Invalid module id '{manifest.Id}'.");
            if (string.IsNullOrWhiteSpace(manifest.Name) || string.IsNullOrWhiteSpace(manifest.Description))
                errors.Add($"Module '{manifest.Id}' requires a name and description.");
            if (!TryParseVersion(manifest.Version, out Version? moduleVersion))
                errors.Add($"Module '{manifest.Id}' has invalid semantic version '{manifest.Version}'.");
            ValidateDistribution(catalog, manifest, errors);
            ValidateCompatibility(manifest, hostVersion, errors);
            EnsureUnique(manifest.Requires, $"required dependency in '{manifest.Id}'", errors);
            EnsureUnique(manifest.OptionalDependencies, $"optional dependency in '{manifest.Id}'", errors);
            EnsureUnique(manifest.Capabilities, $"capability in '{manifest.Id}'", errors);
            foreach (string capability in manifest.Capabilities)
            {
                if (!KnownCapabilities.Contains(capability))
                    errors.Add($"Module '{manifest.Id}' declares unknown capability '{capability}'.");
            }
            if (manifest.Requires.Contains(manifest.Id, StringComparer.Ordinal)
                || manifest.OptionalDependencies.Contains(manifest.Id, StringComparer.Ordinal))
                errors.Add($"Module '{manifest.Id}' cannot depend on itself.");
            string? overlap = manifest.Requires.Intersect(manifest.OptionalDependencies, StringComparer.Ordinal).FirstOrDefault();
            if (overlap is not null)
                errors.Add($"Module '{manifest.Id}' declares '{overlap}' as both required and optional.");

            ValidateEntrypoints(module, webEnabled, errors);
            ValidateContributions(manifest, errors);
        }

        ValidateEnabledGraph(modules, errors);
        ValidateGlobalContributions(modules.Where(module => module.Registration.Enabled), errors);
    }

    private static void ValidateCompatibility(ModuleManifest manifest, Version? hostVersion, List<string> errors)
    {
        if (!TryParseVersion(manifest.Compatibility.MinimumHostVersion, out Version? minimum))
            errors.Add($"Module '{manifest.Id}' has invalid minimum host version '{manifest.Compatibility.MinimumHostVersion}'.");
        if (!TryParseVersion(manifest.Compatibility.MaximumHostVersionExclusive, out Version? maximum))
            errors.Add($"Module '{manifest.Id}' has invalid maximum host version '{manifest.Compatibility.MaximumHostVersionExclusive}'.");
        if (minimum is not null && maximum is not null && minimum >= maximum)
            errors.Add($"Module '{manifest.Id}' has an empty host compatibility range.");
        if (hostVersion is not null && minimum is not null && maximum is not null
            && (hostVersion < minimum || hostVersion >= maximum))
            errors.Add($"Module '{manifest.Id}' is incompatible with Trykatch host {hostVersion}. Supported range is [{minimum}, {maximum}).");
    }

    private void ValidateEntrypoints(LoadedModule module, bool webEnabled, List<string> errors)
    {
        ModuleManifest manifest = module.Manifest;
        if (string.IsNullOrWhiteSpace(manifest.Entrypoints.Dotnet.Type)
            || !DotnetTypeRegex().IsMatch(manifest.Entrypoints.Dotnet.Type))
            errors.Add($"Module '{manifest.Id}' requires a fully qualified .NET registration type.");
        if (string.Equals(manifest.Distribution.Kind, "workspace", StringComparison.Ordinal))
            ValidateArtifact(manifest.Id, manifest.Artifacts.DotnetProject, "dotnet project", errors);

        if (!manifest.Capabilities.Contains("web", StringComparer.Ordinal))
            return;
        if (string.IsNullOrWhiteSpace(manifest.Entrypoints.Web.Specifier)
            || string.IsNullOrWhiteSpace(manifest.Entrypoints.Web.Export)
            || !JavaScriptIdentifierRegex().IsMatch(manifest.Entrypoints.Web.Export))
            errors.Add($"Web module '{manifest.Id}' requires a valid import specifier and export.");
        if (webEnabled && string.Equals(manifest.Distribution.Kind, "workspace", StringComparison.Ordinal))
            ValidateArtifact(manifest.Id, manifest.Artifacts.WebPackage, "web package", errors);
    }

    private void ValidateDistribution(ModuleCatalogFile catalog, ModuleManifest manifest, List<string> errors)
    {
        ModuleDistribution distribution = manifest.Distribution;
        if (!string.Equals(distribution.Kind, "workspace", StringComparison.Ordinal)
            && !string.Equals(distribution.Kind, "package", StringComparison.Ordinal))
            errors.Add($"Module '{manifest.Id}' has unsupported distribution kind '{distribution.Kind}'.");
        if (string.IsNullOrWhiteSpace(distribution.License))
            errors.Add($"Module '{manifest.Id}' must declare its SPDX license expression.");

        if (string.Equals(distribution.Kind, "workspace", StringComparison.Ordinal))
        {
            if (distribution.Dotnet is not null || distribution.Web is not null)
                errors.Add($"Workspace module '{manifest.Id}' must not declare package identities.");
            return;
        }

        if (distribution.Dotnet is null)
            errors.Add($"Package module '{manifest.Id}' must declare its .NET package identity.");
        else
            ValidatePackageIdentity(manifest, distribution.Dotnet, ".NET", errors);

        bool hasWeb = manifest.Capabilities.Contains("web", StringComparer.Ordinal);
        if (hasWeb && distribution.Web is null)
            errors.Add($"Package web module '{manifest.Id}' must declare its web package identity.");
        if (!hasWeb && distribution.Web is not null)
            errors.Add($"Package module '{manifest.Id}' declares a web package without the web capability.");
        if (distribution.Web is not null)
            ValidatePackageIdentity(manifest, distribution.Web, "web", errors);

        if (distribution.Dotnet is not null)
            ValidateInstalledDotnetPackage(catalog, manifest, distribution.Dotnet, errors);
        if (distribution.Web is not null && HasWebSurface())
            ValidateInstalledWebPackage(manifest, distribution.Web, errors);
    }

    private static void ValidatePackageIdentity(
        ModuleManifest manifest,
        ModulePackageIdentity package,
        string ecosystem,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(package.Id))
            errors.Add($"Module '{manifest.Id}' has an empty {ecosystem} package id.");
        if (!TryParseVersion(package.Version, out _))
            errors.Add($"Module '{manifest.Id}' has invalid {ecosystem} package version '{package.Version}'.");
        else if (!string.Equals(package.Version, manifest.Version, StringComparison.Ordinal))
            errors.Add($"Module '{manifest.Id}' {ecosystem} package version must match manifest version '{manifest.Version}'.");
    }

    private void ValidateArtifact(string moduleId, string path, string subject, List<string> errors)
    {
        ValidateRelativePath(path, $"{subject} for '{moduleId}'", errors);
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (!File.Exists(ResolveInsideRoot(path)))
                errors.Add($"Module '{moduleId}' {subject} '{path}' does not exist.");
        }
        catch (InvalidOperationException exception)
        {
            errors.Add(exception.Message);
        }
    }

    private static void ValidateContributions(ModuleManifest manifest, List<string> errors)
    {
        EnsureUnique(manifest.Contributions.Permissions, $"permission in '{manifest.Id}'", errors);
        EnsureUnique(manifest.Contributions.Routes.Select(route => route.Id), $"route id in '{manifest.Id}'", errors);
        EnsureUnique(manifest.Contributions.Routes.Select(route => route.Path), $"route path in '{manifest.Id}'", errors);
        EnsureUnique(manifest.Contributions.ExtensionPoints.Select(point => point.Id), $"extension point in '{manifest.Id}'", errors);
        EnsureUnique(manifest.Contributions.Extensions.Select(extension => extension.Id), $"extension in '{manifest.Id}'", errors);
        EnsureUnique(manifest.Contributions.AssistantTools.Select(tool => tool.Name), $"assistant tool in '{manifest.Id}'", errors);
        EnsureUnique(manifest.Contributions.AssistantTools.Select(tool => tool.OperationId), $"assistant operation in '{manifest.Id}'", errors);

        foreach (string permission in manifest.Contributions.Permissions)
        {
            if (!StableContractIdRegex().IsMatch(permission))
                errors.Add($"Module '{manifest.Id}' declares invalid permission '{permission}'.");
        }
        foreach (ModuleRoute route in manifest.Contributions.Routes)
        {
            if (!StableContractIdRegex().IsMatch(route.Id) || route.Path.Length == 0 || route.Path[0] != '/')
                errors.Add($"Module '{manifest.Id}' declares invalid route '{route.Id}' at '{route.Path}'.");
        }
        foreach (ModuleExtensionPoint point in manifest.Contributions.ExtensionPoints)
        {
            if (!StableContractIdRegex().IsMatch(point.Id))
                errors.Add($"Module '{manifest.Id}' declares invalid extension point '{point.Id}'.");
        }
        foreach (ModuleExtension extension in manifest.Contributions.Extensions)
        {
            if (!StableContractIdRegex().IsMatch(extension.Id) || !StableContractIdRegex().IsMatch(extension.Point))
                errors.Add($"Module '{manifest.Id}' declares invalid extension '{extension.Id}'.");
        }
        foreach (ModuleAssistantTool tool in manifest.Contributions.AssistantTools)
        {
            if (!ToolNameRegex().IsMatch(tool.Name) || !OperationIdRegex().IsMatch(tool.OperationId))
                errors.Add($"Module '{manifest.Id}' declares invalid assistant tool '{tool.Name}'.");
            if (!string.Equals(tool.Risk, "read-only", StringComparison.Ordinal)
                && !tool.RequiresHumanConfirmation)
                errors.Add($"Module '{manifest.Id}' assistant tool '{tool.Name}' changes state without human confirmation.");
        }
    }

    private static void ValidateEnabledGraph(IReadOnlyCollection<LoadedModule> modules, List<string> errors)
    {
        Dictionary<string, LoadedModule> byId = new(StringComparer.Ordinal);
        foreach (LoadedModule module in modules)
            byId.TryAdd(module.Manifest.Id, module);
        foreach (LoadedModule module in modules.Where(module => module.Registration.Enabled))
        {
            foreach (string dependency in module.Manifest.Requires)
            {
                if (!byId.TryGetValue(dependency, out LoadedModule? required))
                    errors.Add($"Enabled module '{module.Manifest.Id}' requires missing module '{dependency}'.");
                else if (!required.Registration.Enabled)
                    errors.Add($"Enabled module '{module.Manifest.Id}' requires disabled module '{dependency}'.");
            }
        }

        HashSet<string> visiting = new(StringComparer.Ordinal);
        HashSet<string> visited = new(StringComparer.Ordinal);
        foreach (LoadedModule module in modules.Where(module => module.Registration.Enabled))
            Visit(module.Manifest.Id);

        void Visit(string id)
        {
            if (visited.Contains(id))
                return;
            if (!visiting.Add(id))
            {
                errors.Add($"Enabled module dependency graph contains a cycle at '{id}'.");
                return;
            }

            if (byId.TryGetValue(id, out LoadedModule? module))
            {
                IEnumerable<string> dependencies = module.Manifest.Requires.Concat(
                    module.Manifest.OptionalDependencies.Where(dependency =>
                        byId.TryGetValue(dependency, out LoadedModule? optional) && optional.Registration.Enabled));
                foreach (string dependency in dependencies)
                    Visit(dependency);
            }

            visiting.Remove(id);
            visited.Add(id);
        }
    }

    private static void ValidateGlobalContributions(IEnumerable<LoadedModule> enabledModules, List<string> errors)
    {
        LoadedModule[] modules = enabledModules.ToArray();
        EnsureUnique(modules.SelectMany(module => module.Manifest.Contributions.Permissions), "permission across enabled modules", errors);
        EnsureUnique(modules.SelectMany(module => module.Manifest.Contributions.Routes.Select(route => route.Id)), "route id across enabled modules", errors);
        EnsureUnique(modules.SelectMany(module => module.Manifest.Contributions.Routes.Select(route => route.Path)), "route path across enabled modules", errors);
        EnsureUnique(modules.SelectMany(module => module.Manifest.Contributions.ExtensionPoints.Select(point => point.Id)), "extension point across enabled modules", errors);
        EnsureUnique(modules.SelectMany(module => module.Manifest.Contributions.Extensions.Select(extension => extension.Id)), "extension across enabled modules", errors);
        EnsureUnique(modules.SelectMany(module => module.Manifest.Contributions.AssistantTools.Select(tool => tool.Name)), "assistant tool across enabled modules", errors);
        EnsureUnique(modules.SelectMany(module => module.Manifest.Contributions.AssistantTools.Select(tool => tool.OperationId)), "assistant operation across enabled modules", errors);

        HashSet<string> points = modules
            .SelectMany(module => module.Manifest.Contributions.ExtensionPoints)
            .Select(point => point.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (ModuleExtension extension in modules.SelectMany(module => module.Manifest.Contributions.Extensions))
        {
            if (!points.Contains(extension.Point))
                errors.Add($"Extension '{extension.Id}' targets unavailable point '{extension.Point}'.");
        }
    }

    private void ValidateGeneratedRegistries(ModuleCatalogFile catalog, IReadOnlyCollection<LoadedModule> modules, List<string> errors)
    {
        ValidateGeneratedFile(
            catalog.Outputs.Backend,
            GenerateDotnetRegistry(
                catalog.Outputs.BackendNamespace,
                catalog.Outputs.DotnetModuleContractNamespace,
                modules),
            "backend module registry",
            errors);
        ValidateGeneratedFile(
            catalog.Outputs.Migrator,
            GenerateDotnetRegistry(
                catalog.Outputs.MigratorNamespace,
                catalog.Outputs.DotnetModuleContractNamespace,
                modules),
            "migrator module registry",
            errors);
        if (HasWebSurface())
            ValidateGeneratedFile(
                catalog.Outputs.Web,
                GenerateWeb(catalog.Outputs.WebModuleSdkSpecifier, modules),
                "web module registry",
                errors);
    }

    private void ValidateGeneratedLock(ModuleCatalogFile catalog, IReadOnlyCollection<LoadedModule> modules, List<string> errors) =>
        ValidateGeneratedFile(catalog.LockFile, GenerateLock(catalog, modules), "module lock file", errors);

    private void ValidateGeneratedFile(string relativePath, string expected, string subject, List<string> errors)
    {
        string path = ResolveInsideRoot(relativePath);
        if (!File.Exists(path))
        {
            errors.Add($"Generated {subject} '{relativePath}' is missing. Run 'trykatch module generate'.");
            return;
        }

        string actual = File.ReadAllText(path);
        if (!string.Equals(NormalizeNewlines(actual), expected, StringComparison.Ordinal))
            errors.Add($"Generated {subject} '{relativePath}' has drifted. Run 'trykatch module generate'.");
    }

    private void WriteGeneratedRegistries(ModuleCatalogFile catalog, IReadOnlyCollection<LoadedModule> modules)
    {
        WriteAtomic(
            ResolveInsideRoot(catalog.Outputs.Backend),
            GenerateDotnetRegistry(
                catalog.Outputs.BackendNamespace,
                catalog.Outputs.DotnetModuleContractNamespace,
                modules));
        WriteAtomic(
            ResolveInsideRoot(catalog.Outputs.Migrator),
            GenerateDotnetRegistry(
                catalog.Outputs.MigratorNamespace,
                catalog.Outputs.DotnetModuleContractNamespace,
                modules));
        if (HasWebSurface())
            WriteAtomic(
                ResolveInsideRoot(catalog.Outputs.Web),
                GenerateWeb(catalog.Outputs.WebModuleSdkSpecifier, modules));
        WriteAtomic(ResolveInsideRoot(catalog.LockFile), GenerateLock(catalog, modules));
    }

    private static string GenerateLock(ModuleCatalogFile catalog, IEnumerable<LoadedModule> modules)
    {
        ModuleLockFile lockFile = new()
        {
            SchemaVersion = SupportedSchemaVersion,
            HostVersion = catalog.HostVersion,
            Modules = modules
                .OrderBy(module => module.Manifest.Id, StringComparer.Ordinal)
                .Select(module => new ModuleLockEntry
                {
                    Id = module.Manifest.Id,
                    Version = module.Manifest.Version,
                    Enabled = module.Registration.Enabled,
                    ManifestSha256 = module.ManifestSha256,
                    ManifestDigestMode = module.ManifestDigestMode,
                    Distribution = module.Manifest.Distribution
                })
                .ToList()
        };
        return JsonSerializer.Serialize(lockFile, JsonOptions) + "\n";
    }

    private static string GenerateDotnetRegistry(
        string registryNamespace,
        string moduleContractNamespace,
        IEnumerable<LoadedModule> modules)
    {
        LoadedModule[] enabled = OrderEnabled(modules).ToArray();
        string[] namespaces = enabled
            .Select(module => module.Manifest.Entrypoints.Dotnet.Type[..module.Manifest.Entrypoints.Dotnet.Type.LastIndexOf('.')])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        StringBuilder output = new();
        output.AppendLine("// <auto-generated />");
        output.AppendLine("// Generated by 'trykatch module generate'. Do not edit by hand.");
        foreach (string item in namespaces)
            output.Append("using ").Append(item).AppendLine(";");
        output.Append("using ").Append(moduleContractNamespace).AppendLine(";");
        output.AppendLine();
        output.Append("namespace ").Append(registryNamespace).AppendLine(";");
        output.AppendLine();
        output.AppendLine("internal static class EnabledModules");
        output.AppendLine("{");
        output.AppendLine("    public static IReadOnlyList<ITrykatchModule> All { get; } =");
        output.AppendLine("    [");
        foreach (LoadedModule module in enabled)
        {
            string type = module.Manifest.Entrypoints.Dotnet.Type[(module.Manifest.Entrypoints.Dotnet.Type.LastIndexOf('.') + 1)..];
            output.Append("        new ").Append(type).AppendLine("(),");
        }
        output.AppendLine("    ];");
        output.AppendLine("}");
        return output.ToString();
    }

    private static string GenerateWeb(string moduleSdkSpecifier, IEnumerable<LoadedModule> modules)
    {
        LoadedModule[] enabled = OrderEnabled(modules)
            .Where(module => module.Manifest.Capabilities.Contains("web", StringComparer.Ordinal))
            .ToArray();
        StringBuilder output = new();
        output.AppendLine("// Generated by 'trykatch module generate'. Do not edit by hand.");
        output.Append("import { TrykatchWebModuleCatalog } from '").Append(moduleSdkSpecifier).AppendLine("'");
        foreach (LoadedModule module in enabled)
        {
            output.Append("import { ").Append(module.Manifest.Entrypoints.Web.Export).Append(" } from '")
                .Append(module.Manifest.Entrypoints.Web.Specifier).AppendLine("'");
        }
        output.AppendLine("import { workspaceOverrides } from './module-overrides'");
        output.AppendLine();
        output.AppendLine("export const workspaceModules = new TrykatchWebModuleCatalog([");
        foreach (LoadedModule module in enabled)
            output.Append("  ").Append(module.Manifest.Entrypoints.Web.Export).AppendLine(",");
        output.AppendLine("], workspaceOverrides)");
        return output.ToString();
    }

    private static List<LoadedModule> OrderEnabled(IEnumerable<LoadedModule> modules)
    {
        Dictionary<string, LoadedModule> byId = modules
            .Where(module => module.Registration.Enabled)
            .ToDictionary(module => module.Manifest.Id, StringComparer.Ordinal);
        List<LoadedModule> ordered = [];
        HashSet<string> visited = new(StringComparer.Ordinal);

        foreach (string id in byId.Keys.Order(StringComparer.Ordinal))
            Visit(id);
        return ordered;

        void Visit(string id)
        {
            if (!visited.Add(id) || !byId.TryGetValue(id, out LoadedModule? module))
                return;
            IEnumerable<string> dependencies = module.Manifest.Requires.Concat(
                module.Manifest.OptionalDependencies.Where(byId.ContainsKey));
            foreach (string dependency in dependencies.Order(StringComparer.Ordinal))
                Visit(dependency);
            ordered.Add(module);
        }
    }

    private Dictionary<string, byte[]?> CaptureFiles(ModuleCatalogFile catalog)
    {
        string[] paths = HasWebSurface()
            ?
            [
                _catalogPath,
                ResolveInsideRoot(catalog.Outputs.Backend),
                ResolveInsideRoot(catalog.Outputs.Migrator),
                ResolveInsideRoot(catalog.Outputs.Web),
                ResolveInsideRoot(catalog.LockFile)
            ]
            :
            [
                _catalogPath,
                ResolveInsideRoot(catalog.Outputs.Backend),
                ResolveInsideRoot(catalog.Outputs.Migrator),
                ResolveInsideRoot(catalog.LockFile)
            ];
        return paths.ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null, StringComparer.Ordinal);
    }

    private static void RestoreFiles(IReadOnlyDictionary<string, byte[]?> originals)
    {
        foreach ((string path, byte[]? content) in originals)
        {
            if (content is null)
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, content);
            }
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, NormalizeNewlines(content), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private T? ReadJson<T>(string path, List<string> errors, string subject)
    {
        if (!File.Exists(path))
        {
            errors.Add($"Missing {subject} '{Path.GetRelativePath(_root, path)}'.");
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException exception)
        {
            errors.Add($"Invalid {subject} JSON: {exception.Message}");
            return default;
        }
    }

    private void ValidateOutputPath(string path, string subject, List<string> errors) =>
        ValidateRelativePath(path, subject, errors);

    private void ValidateRelativePath(string path, string subject, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            errors.Add($"The {subject} path must be a non-empty path relative to the Trykatch workspace.");
            return;
        }

        try
        {
            _ = ResolveInsideRoot(path);
        }
        catch (InvalidOperationException exception)
        {
            errors.Add(exception.Message);
        }
    }

    private string ResolveInsideRoot(string path)
    {
        string resolved = Path.GetFullPath(path, _root);
        string rootPrefix = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!resolved.StartsWith(rootPrefix, pathComparison) && !string.Equals(resolved, _root, pathComparison))
            throw new InvalidOperationException($"Path '{path}' escapes the Trykatch workspace.");
        return resolved;
    }

    private string ResolveHostProject(string generatedRegistryPath, string registryNamespace, string subject)
    {
        string registryPath = ResolveInsideRoot(generatedRegistryPath);
        string? hostDirectory = Directory.GetParent(registryPath)?.Parent?.FullName;
        string[] projects = hostDirectory is not null && Directory.Exists(hostDirectory)
            ? Directory.GetFiles(hostDirectory, "*.csproj", SearchOption.TopDirectoryOnly)
            : [];
        if (projects.Length == 0 && registryNamespace.EndsWith(".Modules", StringComparison.Ordinal))
        {
            string hostNamespace = registryNamespace[..^".Modules".Length];
            projects = Directory.GetFiles(_root, $"{hostNamespace}.csproj", SearchOption.AllDirectories);
        }
        if (projects.Length != 1)
            throw new InvalidOperationException(
                $"Expected exactly one {subject} host project beside registry '{generatedRegistryPath}', found {projects.Length}.");
        return projects[0];
    }

    private string ResolveSolution()
    {
        string[] solutions = Directory.GetFiles(_root, "*.slnx", SearchOption.TopDirectoryOnly);
        if (solutions.Length != 1)
            throw new InvalidOperationException($"Expected exactly one solution at the Trykatch workspace root, found {solutions.Length}.");
        return solutions[0];
    }

    private bool HasWebSurface() => Directory.Exists(Path.Combine(_root, "web"));

    private static void EnsureUnique(IEnumerable<string> values, string subject, List<string> errors)
    {
        string? duplicate = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
            errors.Add($"Duplicate {subject} '{duplicate}'.");
    }

    private static bool TryParseVersion(string value, out Version? version)
    {
        version = null;
        if (!SemanticVersionRegex().IsMatch(value))
            return false;
        version = Version.Parse(value);
        return true;
    }

    private static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string ComputeFileSha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string ComputeWorkspaceManifestSha256(
        string path,
        ModuleCatalogFile catalog,
        ModuleManifest manifest)
    {
        const string backendNamespaceSuffix = ".Api.Modules";
        // Keep the canonical tokens split so dotnet template source-name replacement
        // does not rewrite the normalizer inside a generated ModuleTool.
        const string templateNamespace = "Trykatch" + "App";
        const string templateNpmScope = "trykatch" + "app";
        string contents = File.ReadAllText(path);
        string applicationNamespace = catalog.Outputs.BackendNamespace.EndsWith(
            backendNamespaceSuffix,
            StringComparison.Ordinal)
            ? catalog.Outputs.BackendNamespace[..^backendNamespaceSuffix.Length]
            : string.Empty;
        if (!string.IsNullOrWhiteSpace(applicationNamespace))
        {
            contents = contents.Replace(applicationNamespace, templateNamespace, StringComparison.Ordinal);
            contents = contents.Replace(
                applicationNamespace.ToLowerInvariant(),
                templateNpmScope,
                StringComparison.Ordinal);

            string webSpecifier = manifest.Entrypoints.Web.Specifier;
            int scopeEnd = webSpecifier.IndexOf('/', StringComparison.Ordinal);
            bool isApplicationOwned = manifest.Entrypoints.Dotnet.Type.StartsWith(
                applicationNamespace + ".",
                StringComparison.Ordinal);
            if (isApplicationOwned && webSpecifier.StartsWith('@') && scopeEnd > 1)
            {
                contents = contents.Replace(
                    webSpecifier[..scopeEnd],
                    "@" + templateNpmScope,
                    StringComparison.Ordinal);
            }
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contents))).ToLowerInvariant();
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex StableIdRegex();

    [GeneratedRegex("^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex StableContractIdRegex();

    [GeneratedRegex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersionRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*(?:\\.[A-Za-z_][A-Za-z0-9_]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex DotnetTypeRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*(?:\\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex DotnetNamespaceRegex();

    [GeneratedRegex("^[A-Za-z_$][A-Za-z0-9_$]*$", RegexOptions.CultureInvariant)]
    private static partial Regex JavaScriptIdentifierRegex();

    [GeneratedRegex("^[a-z][a-z0-9]*(?:_[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ToolNameRegex();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex OperationIdRegex();

    private sealed record LoadedModule(
        ModuleRegistration Registration,
        ModuleManifest Manifest,
        string ManifestSha256,
        string ManifestDigestMode);
}

public sealed record ModuleDoctorReport(IReadOnlyList<ModuleStatus> Modules, IReadOnlyList<string> Errors)
{
    public bool IsHealthy => Errors.Count == 0;
}

public sealed record ModuleStatus(string Id, string Name, string Version, bool Enabled, string ManifestPath);

public sealed class ModuleCatalogFile
{
    [JsonPropertyName("$schema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Schema { get; init; } = string.Empty;
    public int SchemaVersion { get; init; }
    public string HostVersion { get; init; } = string.Empty;
    public string LockFile { get; init; } = string.Empty;
    public ModuleCatalogOutputs Outputs { get; init; } = new();
    public List<ModuleRegistration> Modules { get; init; } = [];
}

public sealed class ModuleCatalogOutputs
{
    public string Backend { get; init; } = string.Empty;
    public string BackendNamespace { get; init; } = string.Empty;
    public string DotnetModuleContractNamespace { get; init; } = string.Empty;
    public string Migrator { get; init; } = string.Empty;
    public string MigratorNamespace { get; init; } = string.Empty;
    public string Web { get; init; } = string.Empty;
    public string WebModuleSdkSpecifier { get; init; } = string.Empty;
}

public sealed class ModuleRegistration
{
    public string Id { get; init; } = string.Empty;
    public string Manifest { get; set; } = string.Empty;
    public bool Enabled { get; set; }
}

public sealed class ModuleManifest
{
    [JsonPropertyName("$schema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Schema { get; init; } = string.Empty;
    public int SchemaVersion { get; init; }
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public ModuleDistribution Distribution { get; init; } = new();
    public ModuleCompatibility Compatibility { get; init; } = new();
    public List<string> Requires { get; init; } = [];
    public List<string> OptionalDependencies { get; init; } = [];
    public List<string> Capabilities { get; init; } = [];
    public ModuleArtifacts Artifacts { get; init; } = new();
    public ModuleEntrypoints Entrypoints { get; init; } = new();
    public ModuleContributions Contributions { get; init; } = new();
}

public sealed class ModuleCompatibility
{
    public string MinimumHostVersion { get; init; } = string.Empty;
    public string MaximumHostVersionExclusive { get; init; } = string.Empty;
}

public sealed class ModuleArtifacts
{
    public string DotnetProject { get; init; } = string.Empty;
    public string WebPackage { get; init; } = string.Empty;
}

public sealed class ModuleDistribution
{
    public string Kind { get; init; } = string.Empty;
    public string License { get; init; } = string.Empty;
    public ModulePackageIdentity? Dotnet { get; init; }
    public ModulePackageIdentity? Web { get; init; }
}

public sealed class ModulePackageIdentity
{
    public string Id { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
}

public sealed class ModuleLockFile
{
    public int SchemaVersion { get; init; }
    public string HostVersion { get; init; } = string.Empty;
    public List<ModuleLockEntry> Modules { get; init; } = [];
}

public sealed class ModuleLockEntry
{
    public string Id { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public string ManifestSha256 { get; init; } = string.Empty;
    public string ManifestDigestMode { get; init; } = string.Empty;
    public ModuleDistribution Distribution { get; init; } = new();
}

public sealed class ModuleEntrypoints
{
    public DotnetModuleEntrypoint Dotnet { get; init; } = new();
    public WebModuleEntrypoint Web { get; init; } = new();
}

public sealed class DotnetModuleEntrypoint
{
    public string Type { get; init; } = string.Empty;
}

public sealed class WebModuleEntrypoint
{
    public string Specifier { get; init; } = string.Empty;
    public string Export { get; init; } = string.Empty;
}

public sealed class ModuleContributions
{
    public List<string> Permissions { get; init; } = [];
    public List<ModuleRoute> Routes { get; init; } = [];
    public List<ModuleExtensionPoint> ExtensionPoints { get; init; } = [];
    public List<ModuleExtension> Extensions { get; init; } = [];
    public List<ModuleAssistantTool> AssistantTools { get; init; } = [];
}

public sealed class ModuleRoute
{
    public string Id { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
}

public sealed class ModuleExtensionPoint
{
    public string Id { get; init; } = string.Empty;
}

public sealed class ModuleExtension
{
    public string Id { get; init; } = string.Empty;
    public string Point { get; init; } = string.Empty;
}

public sealed class ModuleAssistantTool
{
    public string Name { get; init; } = string.Empty;
    public string OperationId { get; init; } = string.Empty;
    public string Risk { get; init; } = string.Empty;
    public bool RequiresHumanConfirmation { get; init; }
}
