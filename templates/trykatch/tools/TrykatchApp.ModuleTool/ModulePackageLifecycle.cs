using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TrykatchApp.ModuleTool;

public sealed partial class ModuleWorkspace
{
    public ModuleDoctorReport RegisterWorkspace(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        string absolutePath = Path.GetFullPath(manifestPath, _root);
        string relativePath;
        try
        {
            relativePath = Path.GetRelativePath(_root, ResolveInsideRoot(absolutePath))
                .Replace(Path.DirectorySeparatorChar, '/');
        }
        catch (InvalidOperationException)
        {
            return new([], ["A workspace module manifest must be inside the Trykatch workspace."]);
        }

        List<string> errors = [];
        ModuleManifest? manifest = ReadJson<ModuleManifest>(absolutePath, errors, "workspace module manifest");
        ModuleCatalogFile? catalog = ReadJson<ModuleCatalogFile>(_catalogPath, errors, "module catalog");
        if (manifest is null || catalog is null)
            return new([], errors);
        if (!string.Equals(manifest.Distribution.Kind, "workspace", StringComparison.Ordinal))
            return new([], ["Use 'module install' for a package-distributed module."]);
        if (catalog.Modules.Any(module => string.Equals(module.Id, manifest.Id, StringComparison.Ordinal)))
            return new([], [$"Trykatch module '{manifest.Id}' is already registered."]);

        ValidateCatalog(catalog, errors);
        catalog.Modules.Add(new() { Id = manifest.Id, Manifest = relativePath, Enabled = false });
        List<LoadedModule> modules = LoadModules(catalog, errors);
        ValidateModules(catalog, modules, errors);
        if (errors.Count > 0)
            return ToReport(modules, errors);

        Dictionary<string, byte[]?> originals = CaptureFiles(catalog);
        try
        {
            WriteGeneratedRegistries(catalog, modules);
            WriteAtomic(_catalogPath, JsonSerializer.Serialize(catalog, JsonOptions) + "\n");
        }
        catch
        {
            RestoreFiles(originals);
            throw;
        }
        return ToReport(modules, []);
    }

    public ModuleDoctorReport InstallPackage(string manifestPath, string expectedSha256)
    {
        CandidatePackage candidate = ReadCandidatePackage(manifestPath, expectedSha256);
        List<string> errors = [];
        ModuleCatalogFile? catalog = ReadJson<ModuleCatalogFile>(_catalogPath, errors, "module catalog");
        if (catalog is null)
            return new([], errors);
        ValidateCatalog(catalog, errors);
        if (errors.Count > 0)
            return new([], errors);
        if (catalog.Modules.Any(module => string.Equals(module.Id, candidate.Manifest.Id, StringComparison.Ordinal)))
            return new([], [$"Trykatch module '{candidate.Manifest.Id}' is already installed. Use 'module upgrade' for a package update."]);

        string destination = PackageManifestPath(candidate.Manifest);
        ModuleRegistration registration = new()
        {
            Id = candidate.Manifest.Id,
            Manifest = Path.GetRelativePath(_root, destination).Replace(Path.DirectorySeparatorChar, '/'),
            Enabled = false
        };
        catalog.Modules.Add(registration);

        return MutatePackageWorkspace(catalog, candidate, destination, previousManifestPath: null, remove: false);
    }

    public ModuleDoctorReport UpgradePackage(string manifestPath, string expectedSha256)
    {
        CandidatePackage candidate = ReadCandidatePackage(manifestPath, expectedSha256);
        List<string> errors = [];
        ModuleCatalogFile? catalog = ReadJson<ModuleCatalogFile>(_catalogPath, errors, "module catalog");
        if (catalog is null)
            return new([], errors);
        ValidateCatalog(catalog, errors);
        List<LoadedModule> currentModules = LoadModules(catalog, errors);
        ValidateModules(catalog, currentModules, errors);
        if (errors.Count > 0)
            return ToReport(currentModules, errors);

        LoadedModule? current = currentModules.SingleOrDefault(module =>
            string.Equals(module.Manifest.Id, candidate.Manifest.Id, StringComparison.Ordinal));
        if (current is null)
            return ToReport(currentModules, [$"Trykatch module '{candidate.Manifest.Id}' is not installed."]);
        if (!string.Equals(current.Manifest.Distribution.Kind, "package", StringComparison.Ordinal))
            return ToReport(currentModules, [$"Workspace module '{candidate.Manifest.Id}' is source-owned and cannot be upgraded as a package."]);
        if (!TryParseVersion(current.Manifest.Version, out Version? existingVersion)
            || !TryParseVersion(candidate.Manifest.Version, out Version? candidateVersion)
            || candidateVersion <= existingVersion)
            return ToReport(currentModules,
            [
                $"Package upgrade for '{candidate.Manifest.Id}' must increase the version beyond '{current.Manifest.Version}'."
            ]);
        if (!SamePackageIds(current.Manifest.Distribution, candidate.Manifest.Distribution))
            return ToReport(currentModules,
            [
                $"Package upgrade for '{candidate.Manifest.Id}' cannot change its .NET or web package identity. Unregister and review it as a new supply-chain source instead."
            ]);

        string previousManifestPath = ResolveInsideRoot(current.Registration.Manifest);
        string destination = PackageManifestPath(candidate.Manifest);
        current.Registration.Manifest = Path.GetRelativePath(_root, destination).Replace(Path.DirectorySeparatorChar, '/');
        return MutatePackageWorkspace(catalog, candidate, destination, previousManifestPath, remove: false);
    }

    public ModuleDoctorReport Unregister(string moduleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        List<string> errors = [];
        ModuleCatalogFile? catalog = ReadJson<ModuleCatalogFile>(_catalogPath, errors, "module catalog");
        if (catalog is null)
            return new([], errors);
        ValidateCatalog(catalog, errors);
        List<LoadedModule> modules = LoadModules(catalog, errors);
        ValidateModules(catalog, modules, errors);
        if (errors.Count > 0)
            return ToReport(modules, errors);

        LoadedModule? target = modules.SingleOrDefault(module =>
            string.Equals(module.Manifest.Id, moduleId, StringComparison.Ordinal));
        if (target is null)
            return ToReport(modules, [$"Unknown Trykatch module '{moduleId}'."]);
        if (target.Registration.Enabled)
            return ToReport(modules, [$"Module '{moduleId}' must be disabled before it can be unregistered."]);

        string[] dependents = modules
            .Where(module => module.Manifest.Requires.Contains(moduleId, StringComparer.Ordinal))
            .Select(module => module.Manifest.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (dependents.Length > 0)
            return ToReport(modules,
            [
                $"Module '{moduleId}' is required by installed module(s): {string.Join(", ", dependents)}. Unregister dependents first."
            ]);
        if (catalog.Modules.Count == 1)
            return ToReport(modules, ["The final Trykatch module cannot be unregistered from the catalog."]);

        catalog.Modules.Remove(target.Registration);
        CandidatePackage? package = string.Equals(target.Manifest.Distribution.Kind, "package", StringComparison.Ordinal)
            ? new(target.Manifest, target.ManifestSha256, File.ReadAllBytes(ResolveInsideRoot(target.Registration.Manifest)))
            : null;
        return MutatePackageWorkspace(
            catalog,
            package,
            destinationManifestPath: null,
            previousManifestPath: package is null ? null : ResolveInsideRoot(target.Registration.Manifest),
            remove: true);
    }

    private ModuleDoctorReport MutatePackageWorkspace(
        ModuleCatalogFile catalog,
        CandidatePackage? candidate,
        string? destinationManifestPath,
        string? previousManifestPath,
        bool remove)
    {
        HashSet<string> baselineLockFiles = Directory
            .EnumerateFiles(_root, "packages.lock.json", SearchOption.AllDirectories)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> paths = MutationPaths(catalog, destinationManifestPath, previousManifestPath, baselineLockFiles);
        Dictionary<string, byte[]?> originals = CapturePaths(paths);

        try
        {
            if (destinationManifestPath is not null && candidate is not null)
                WriteAtomicBytes(destinationManifestPath, candidate.ManifestBytes);

            if (candidate is not null)
            {
                if (remove)
                    RemovePackageReferences(candidate.Manifest, catalog);
                else
                    UpsertPackageReferences(candidate.Manifest);
            }

            List<string> errors = [];
            ValidateCatalog(catalog, errors);
            List<LoadedModule> modules = LoadModules(catalog, errors);
            ValidateModules(catalog, modules, errors);
            if (errors.Count > 0)
            {
                RestoreFiles(originals);
                DeleteNewLockFiles(baselineLockFiles);
                return ToReport(modules, errors);
            }

            WriteGeneratedRegistries(catalog, modules);
            WriteAtomic(_catalogPath, JsonSerializer.Serialize(catalog, JsonOptions) + "\n");
            if (candidate is not null)
                RestorePackageGraphs(candidate.Manifest.Distribution.Web is not null);

            ModuleDoctorReport report = Inspect();
            if (!report.IsHealthy)
                throw new InvalidOperationException(string.Join(Environment.NewLine, report.Errors));

            if (previousManifestPath is not null
                && !string.Equals(previousManifestPath, destinationManifestPath, StringComparison.Ordinal)
                && File.Exists(previousManifestPath))
                File.Delete(previousManifestPath);
            return report;
        }
        catch
        {
            RestoreFiles(originals);
            DeleteNewLockFiles(baselineLockFiles);
            throw;
        }
    }

    private static CandidatePackage ReadCandidatePackage(string manifestPath, string expectedSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        if (expectedSha256.Length != 64 || expectedSha256.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("--sha256 must be a 64-character SHA-256 digest.");

        string absolutePath = Path.GetFullPath(manifestPath);
        if (!File.Exists(absolutePath))
            throw new FileNotFoundException($"Package manifest '{absolutePath}' does not exist.", absolutePath);
        byte[] bytes = File.ReadAllBytes(absolutePath);
        string actualSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Package manifest integrity check failed. Expected '{expectedSha256.ToLowerInvariant()}', received '{actualSha256}'.");

        ModuleManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ModuleManifest>(bytes, JsonOptions)
                ?? throw new JsonException("The package manifest was empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Invalid package manifest JSON: {exception.Message}", exception);
        }
        if (!string.Equals(manifest.Distribution.Kind, "package", StringComparison.Ordinal))
            throw new InvalidOperationException("Only a manifest with distribution kind 'package' can be installed or upgraded.");
        return new(manifest, actualSha256, bytes);
    }

    private string PackageManifestPath(ModuleManifest manifest) => ResolveInsideRoot(
        Path.Combine(".trykatch", "modules", manifest.Id, manifest.Version, "trykatch.module.json"));

    private void ValidateInstalledDotnetPackage(
        ModuleManifest manifest,
        ModulePackageIdentity package,
        List<string> errors)
    {
        string centralPath = ResolveInsideRoot("Directory.Packages.props");
        string apiPath = ResolveInsideRoot("src/TrykatchApp.Api/TrykatchApp.Api.csproj");
        string migratorPath = ResolveInsideRoot("src/TrykatchApp.Migrator/TrykatchApp.Migrator.csproj");
        if (!HasPackageVersion(centralPath, package.Id, package.Version))
            errors.Add($"Package module '{manifest.Id}' requires exact central .NET package '{package.Id}' version '{package.Version}'.");
        if (!HasPackageReference(apiPath, package.Id))
            errors.Add($"Package module '{manifest.Id}' is not referenced by the API host.");
        if (!HasPackageReference(migratorPath, package.Id))
            errors.Add($"Package module '{manifest.Id}' is not referenced by the migrator host.");
    }

    private void ValidateInstalledWebPackage(
        ModuleManifest manifest,
        ModulePackageIdentity package,
        List<string> errors)
    {
        string packageJsonPath = ResolveInsideRoot("web/apps/web/package.json");
        if (!HasWebDependency(packageJsonPath, package.Id, package.Version))
            errors.Add($"Package module '{manifest.Id}' requires exact web package '{package.Id}' version '{package.Version}'.");
    }

    private void UpsertPackageReferences(ModuleManifest manifest)
    {
        ModulePackageIdentity dotnet = manifest.Distribution.Dotnet
            ?? throw new InvalidOperationException($"Package module '{manifest.Id}' has no .NET package identity.");
        UpsertPackageVersion(ResolveInsideRoot("Directory.Packages.props"), dotnet.Id, dotnet.Version);
        UpsertPackageReference(ResolveInsideRoot("src/TrykatchApp.Api/TrykatchApp.Api.csproj"), dotnet.Id);
        UpsertPackageReference(ResolveInsideRoot("src/TrykatchApp.Migrator/TrykatchApp.Migrator.csproj"), dotnet.Id);
        if (manifest.Distribution.Web is not null && HasWebSurface())
            UpsertWebDependency(
                ResolveInsideRoot("web/apps/web/package.json"),
                manifest.Distribution.Web.Id,
                manifest.Distribution.Web.Version);
    }

    private void RemovePackageReferences(ModuleManifest manifest, ModuleCatalogFile remainingCatalog)
    {
        ModulePackageIdentity dotnet = manifest.Distribution.Dotnet
            ?? throw new InvalidOperationException($"Package module '{manifest.Id}' has no .NET package identity.");
        List<string> errors = [];
        List<LoadedModule> remaining = LoadModules(remainingCatalog, errors);
        bool dotnetStillUsed = remaining.Any(module =>
            string.Equals(module.Manifest.Distribution.Dotnet?.Id, dotnet.Id, StringComparison.OrdinalIgnoreCase));
        if (!dotnetStillUsed)
        {
            RemovePackageVersion(ResolveInsideRoot("Directory.Packages.props"), dotnet.Id);
            RemovePackageReference(ResolveInsideRoot("src/TrykatchApp.Api/TrykatchApp.Api.csproj"), dotnet.Id);
            RemovePackageReference(ResolveInsideRoot("src/TrykatchApp.Migrator/TrykatchApp.Migrator.csproj"), dotnet.Id);
        }

        if (manifest.Distribution.Web is not null && HasWebSurface())
        {
            bool webStillUsed = remaining.Any(module => string.Equals(
                module.Manifest.Distribution.Web?.Id,
                manifest.Distribution.Web.Id,
                StringComparison.OrdinalIgnoreCase));
            if (!webStillUsed)
                RemoveWebDependency(ResolveInsideRoot("web/apps/web/package.json"), manifest.Distribution.Web.Id);
        }
    }

    private void RestorePackageGraphs(bool includeWeb)
    {
        WorkspaceCommandResult dotnet = _commandRunner.Run(
            "dotnet",
            ["restore", "TrykatchApp.slnx", "--force-evaluate"],
            _root);
        if (dotnet.ExitCode != 0)
            throw new InvalidOperationException($".NET package restore failed:{Environment.NewLine}{dotnet.Output}");

        if (!includeWeb || !HasWebSurface())
            return;
        WorkspaceCommandResult pnpm = _commandRunner.Run(
            "pnpm",
            ["install", "--lockfile-only", "--ignore-scripts"],
            ResolveInsideRoot("web"));
        if (pnpm.ExitCode != 0)
            throw new InvalidOperationException($"Web package restore failed:{Environment.NewLine}{pnpm.Output}");
    }

    private HashSet<string> MutationPaths(
        ModuleCatalogFile catalog,
        string? destinationManifestPath,
        string? previousManifestPath,
        IEnumerable<string> packageLocks)
    {
        HashSet<string> paths =
        [
            _catalogPath,
            ResolveInsideRoot(catalog.LockFile),
            ResolveInsideRoot(catalog.Outputs.Backend),
            ResolveInsideRoot(catalog.Outputs.Migrator),
            ResolveInsideRoot("Directory.Packages.props"),
            ResolveInsideRoot("src/TrykatchApp.Api/TrykatchApp.Api.csproj"),
            ResolveInsideRoot("src/TrykatchApp.Migrator/TrykatchApp.Migrator.csproj")
        ];
        if (HasWebSurface())
        {
            paths.Add(ResolveInsideRoot(catalog.Outputs.Web));
            paths.Add(ResolveInsideRoot("web/apps/web/package.json"));
            paths.Add(ResolveInsideRoot("web/pnpm-lock.yaml"));
        }
        if (destinationManifestPath is not null)
            paths.Add(destinationManifestPath);
        if (previousManifestPath is not null)
            paths.Add(previousManifestPath);
        paths.UnionWith(packageLocks);
        return paths;
    }

    private static Dictionary<string, byte[]?> CapturePaths(IEnumerable<string> paths) => paths
        .Distinct(StringComparer.Ordinal)
        .ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null, StringComparer.Ordinal);

    private void DeleteNewLockFiles(HashSet<string> baseline)
    {
        foreach (string path in Directory.EnumerateFiles(_root, "packages.lock.json", SearchOption.AllDirectories))
        {
            if (!baseline.Contains(path))
                File.Delete(path);
        }
    }

    private static void WriteAtomicBytes(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static bool SamePackageIds(ModuleDistribution left, ModuleDistribution right) =>
        string.Equals(left.Dotnet?.Id, right.Dotnet?.Id, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Web?.Id, right.Web?.Id, StringComparison.OrdinalIgnoreCase);

    private static bool HasPackageVersion(string path, string packageId, string version) =>
        File.Exists(path) && XDocument.Load(path).Descendants("PackageVersion").Any(element =>
            string.Equals((string?)element.Attribute("Include"), packageId, StringComparison.OrdinalIgnoreCase)
            && string.Equals((string?)element.Attribute("Version"), version, StringComparison.Ordinal));

    private static bool HasPackageReference(string path, string packageId) =>
        File.Exists(path) && XDocument.Load(path).Descendants("PackageReference").Any(element =>
            string.Equals((string?)element.Attribute("Include"), packageId, StringComparison.OrdinalIgnoreCase));

    private static void UpsertPackageVersion(string path, string packageId, string version) =>
        UpsertXmlItem(path, "PackageVersion", packageId, element => element.SetAttributeValue("Version", version));

    private static void UpsertPackageReference(string path, string packageId) =>
        UpsertXmlItem(path, "PackageReference", packageId, _ => { });

    private static void RemovePackageVersion(string path, string packageId) => RemoveXmlItem(path, "PackageVersion", packageId);
    private static void RemovePackageReference(string path, string packageId) => RemoveXmlItem(path, "PackageReference", packageId);

    private static void UpsertXmlItem(string path, string elementName, string packageId, Action<XElement> update)
    {
        XDocument document = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        XElement? existing = document.Descendants(elementName).SingleOrDefault(element =>
            string.Equals((string?)element.Attribute("Include"), packageId, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            XElement? group = document.Root?.Elements("ItemGroup")
                .FirstOrDefault(item => item.Elements(elementName).Any())
                ?? document.Root?.Elements("ItemGroup").LastOrDefault();
            if (group is null)
            {
                group = new XElement("ItemGroup");
                document.Root?.Add(group);
            }
            existing = new XElement(elementName, new XAttribute("Include", packageId));
            group.Add(existing);
        }
        update(existing);
        document.Save(path, SaveOptions.DisableFormatting);
    }

    private static void RemoveXmlItem(string path, string elementName, string packageId)
    {
        XDocument document = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        document.Descendants(elementName)
            .Where(element => string.Equals((string?)element.Attribute("Include"), packageId, StringComparison.OrdinalIgnoreCase))
            .Remove();
        document.Save(path, SaveOptions.DisableFormatting);
    }

    private static bool HasWebDependency(string path, string packageId, string version)
    {
        if (!File.Exists(path))
            return false;
        JsonNode? root = JsonNode.Parse(File.ReadAllText(path));
        return string.Equals(root?["dependencies"]?[packageId]?.GetValue<string>(), version, StringComparison.Ordinal);
    }

    private static void UpsertWebDependency(string path, string packageId, string version)
    {
        JsonObject root = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidOperationException($"Invalid web package file '{path}'.");
        JsonObject dependencies = root["dependencies"]?.AsObject() ?? new JsonObject();
        root["dependencies"] = dependencies;
        dependencies[packageId] = version;
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
    }

    private static void RemoveWebDependency(string path, string packageId)
    {
        JsonObject root = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidOperationException($"Invalid web package file '{path}'.");
        root["dependencies"]?.AsObject().Remove(packageId);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
    }

    private sealed record CandidatePackage(ModuleManifest Manifest, string ManifestSha256, byte[] ManifestBytes);
}

internal sealed record WorkspaceCommandResult(int ExitCode, string Output);

internal interface IWorkspaceCommandRunner
{
    WorkspaceCommandResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory);
}

internal sealed class ProcessWorkspaceCommandRunner : IWorkspaceCommandRunner
{
    public WorkspaceCommandResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        ProcessStartInfo startInfo = new(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(standardOutput, standardError);
        return new(process.ExitCode, standardOutput.Result + standardError.Result);
    }
}
