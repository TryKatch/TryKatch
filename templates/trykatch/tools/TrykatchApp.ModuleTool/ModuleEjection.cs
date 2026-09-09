using System.Text.Json;
using System.Xml.Linq;

namespace TrykatchApp.ModuleTool;

public sealed partial class ModuleWorkspace
{
    public ModuleDoctorReport EjectPackage(string moduleId, string sourceBundleRoot, string expectedManifestSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceBundleRoot);
        string bundleRoot = Path.GetFullPath(sourceBundleRoot);
        string bundleManifestPath = Path.Combine(bundleRoot, "trykatch.module.json");
        CandidateSource source = ReadCandidateSource(bundleManifestPath, expectedManifestSha256);
        if (!string.Equals(source.Manifest.Id, moduleId, StringComparison.Ordinal))
            return new([], [$"Source bundle module '{source.Manifest.Id}' does not match installed module '{moduleId}'."]);

        List<string> errors = [];
        ModuleCatalogFile? catalog = ReadJson<ModuleCatalogFile>(_catalogPath, errors, "module catalog");
        if (catalog is null)
            return new([], errors);
        ValidateCatalog(catalog, errors);
        List<LoadedModule> modules = LoadModules(catalog, errors);
        ValidateModules(catalog, modules, errors);
        if (errors.Count > 0)
            return ToReport(modules, errors);

        LoadedModule? installed = modules.SingleOrDefault(module => module.Manifest.Id == moduleId);
        if (installed is null)
            return ToReport(modules, [$"Unknown Trykatch module '{moduleId}'."]);
        if (installed.Registration.Enabled)
            return ToReport(modules, [$"Module '{moduleId}' must be disabled before ejection changes its build inputs."]);
        if (!string.Equals(installed.Manifest.Distribution.Kind, "package", StringComparison.Ordinal))
            return ToReport(modules, [$"Module '{moduleId}' is already workspace-owned."]);
        if (!string.Equals(installed.Manifest.Version, source.Manifest.Version, StringComparison.Ordinal))
            return ToReport(modules,
            [
                $"Source bundle version '{source.Manifest.Version}' does not match installed package version '{installed.Manifest.Version}'."
            ]);

        string dotnetSource = SourceArtifactPath(bundleRoot, source.Manifest.Artifacts.DotnetProject);
        string dotnetTarget = ResolveInsideRoot(source.Manifest.Artifacts.DotnetProject);
        string? webSource = source.Manifest.Capabilities.Contains("web", StringComparer.Ordinal)
            ? SourceArtifactPath(bundleRoot, source.Manifest.Artifacts.WebPackage)
            : null;
        string? webTarget = source.Manifest.Capabilities.Contains("web", StringComparer.Ordinal)
            ? ResolveInsideRoot(source.Manifest.Artifacts.WebPackage)
            : null;
        string dotnetTargetDirectory = Path.GetDirectoryName(dotnetTarget)!;
        string? webTargetDirectory = webTarget is null ? null : Path.GetDirectoryName(webTarget)!;
        if (Directory.Exists(dotnetTargetDirectory) || (webTargetDirectory is not null && Directory.Exists(webTargetDirectory)))
            return ToReport(modules,
            [
                "Ejection refuses to overwrite an existing source directory. Choose clean artifact paths in the reviewed source bundle."
            ]);

        string targetManifestPath = Path.Combine(dotnetTargetDirectory, "trykatch.module.json");
        string oldManifestPath = ResolveInsideRoot(installed.Registration.Manifest);
        HashSet<string> baselineLockFiles = Directory
            .EnumerateFiles(_root, "packages.lock.json", SearchOption.AllDirectories)
            .ToHashSet(StringComparer.Ordinal);
        Dictionary<string, byte[]?> originals = CapturePaths(MutationPaths(
            catalog,
            destinationManifestPath: targetManifestPath,
            previousManifestPath: ResolveInsideRoot(installed.Registration.Manifest),
            baselineLockFiles));

        try
        {
            CopyDirectory(Path.GetDirectoryName(dotnetSource)!, dotnetTargetDirectory);
            if (webSource is not null && webTargetDirectory is not null)
                CopyDirectory(Path.GetDirectoryName(webSource)!, webTargetDirectory);
            WriteAtomicBytes(targetManifestPath, source.ManifestBytes);

            catalog.Modules.Remove(installed.Registration);
            RemovePackageReferences(installed.Manifest, catalog);
            catalog.Modules.Add(installed.Registration);
            AddProjectReference(ResolveInsideRoot("src/TrykatchApp.Api/TrykatchApp.Api.csproj"), dotnetTarget);
            AddProjectReference(ResolveInsideRoot("src/TrykatchApp.Migrator/TrykatchApp.Migrator.csproj"), dotnetTarget);
            if (source.Manifest.Distribution.Web is null && webTarget is not null)
                UpsertWebDependency(
                    ResolveInsideRoot("web/apps/web/package.json"),
                    ReadNpmPackageName(webTarget),
                    "workspace:*");

            installed.Registration.Manifest = Path.GetRelativePath(_root, targetManifestPath)
                .Replace(Path.DirectorySeparatorChar, '/');
            errors.Clear();
            List<LoadedModule> ejectedModules = LoadModules(catalog, errors);
            ValidateModules(catalog, ejectedModules, errors);
            if (errors.Count > 0)
                throw new InvalidOperationException(string.Join(Environment.NewLine, errors));

            WriteGeneratedRegistries(catalog, ejectedModules);
            WriteAtomic(_catalogPath, JsonSerializer.Serialize(catalog, JsonOptions) + "\n");
            RestorePackageGraphs(webTarget is not null);
            ModuleDoctorReport report = Inspect();
            if (!report.IsHealthy)
                throw new InvalidOperationException(string.Join(Environment.NewLine, report.Errors));

            if (File.Exists(oldManifestPath) && !string.Equals(oldManifestPath, targetManifestPath, StringComparison.Ordinal))
                File.Delete(oldManifestPath);
            return report;
        }
        catch
        {
            RestoreFiles(originals);
            DeleteNewLockFiles(baselineLockFiles);
            if (Directory.Exists(dotnetTargetDirectory))
                Directory.Delete(dotnetTargetDirectory, recursive: true);
            if (webTargetDirectory is not null && Directory.Exists(webTargetDirectory))
                Directory.Delete(webTargetDirectory, recursive: true);
            throw;
        }
    }

    private static CandidateSource ReadCandidateSource(string manifestPath, string expectedSha256)
    {
        if (expectedSha256.Length != 64 || expectedSha256.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("--sha256 must be a 64-character SHA-256 digest.");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"Source bundle manifest '{manifestPath}' does not exist.", manifestPath);
        byte[] bytes = File.ReadAllBytes(manifestPath);
        string actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Source bundle manifest integrity check failed.");
        ModuleManifest manifest = JsonSerializer.Deserialize<ModuleManifest>(bytes, JsonOptions)
            ?? throw new InvalidOperationException("Source bundle manifest is empty.");
        if (!string.Equals(manifest.Distribution.Kind, "workspace", StringComparison.Ordinal))
            throw new InvalidOperationException("An ejection source bundle must contain a workspace-distributed manifest.");
        return new(manifest, bytes);
    }

    private static string SourceArtifactPath(string bundleRoot, string workspaceRelativePath)
    {
        if (string.IsNullOrWhiteSpace(workspaceRelativePath) || Path.IsPathRooted(workspaceRelativePath))
            throw new InvalidOperationException("Source bundle artifacts must use workspace-relative paths.");
        string path = Path.GetFullPath(workspaceRelativePath, bundleRoot);
        string prefix = bundleRoot.EndsWith(Path.DirectorySeparatorChar) ? bundleRoot : bundleRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Source bundle artifact path escapes the bundle.");
        if (!File.Exists(path))
            throw new InvalidOperationException($"Source bundle artifact '{workspaceRelativePath}' does not exist.");
        return path;
    }

    private static void AddProjectReference(string hostProject, string moduleProject)
    {
        string include = Path.GetRelativePath(Path.GetDirectoryName(hostProject)!, moduleProject)
            .Replace(Path.DirectorySeparatorChar, '/');
        XDocument document = XDocument.Load(hostProject, LoadOptions.PreserveWhitespace);
        if (document.Descendants("ProjectReference").Any(element =>
                string.Equals((string?)element.Attribute("Include"), include, StringComparison.OrdinalIgnoreCase)))
            return;
        XElement? group = document.Root?.Elements("ItemGroup")
            .FirstOrDefault(item => item.Elements("ProjectReference").Any());
        if (group is null)
        {
            group = new XElement("ItemGroup");
            document.Root?.Add(group);
        }
        group.Add(new XElement("ProjectReference", new XAttribute("Include", include)));
        document.Save(hostProject, SaveOptions.DisableFormatting);
    }

    private static string ReadNpmPackageName(string packageJsonPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
        return document.RootElement.GetProperty("name").GetString()
            ?? throw new InvalidOperationException("Ejected web package requires a name.");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private sealed record CandidateSource(ModuleManifest Manifest, byte[] ManifestBytes);
}
