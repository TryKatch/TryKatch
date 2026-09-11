using System.Text.Json;
using System.Text;
using System.Xml.Linq;

namespace Trykatch.ModuleTool;

public sealed partial class ModuleWorkspace
{
    public ModuleDoctorReport EjectPackage(string moduleId, string sourceBundleRoot, string expectedManifestSha256)
    {
        using IDisposable mutationLock = AcquirePackageMutationLock();
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceBundleRoot);
        string bundleRoot = Path.GetFullPath(sourceBundleRoot);
        string bundleManifestPath = Path.Combine(bundleRoot, "try" + "katch.module.json");
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
        bool hasSourceRoot = !string.IsNullOrWhiteSpace(source.Manifest.Artifacts.SourceRoot);
        string? sourceRoot = hasSourceRoot
            ? SourceDirectoryPath(bundleRoot, source.Manifest.Artifacts.SourceRoot)
            : null;
        string? targetRoot = hasSourceRoot
            ? ResolveInsideRoot(source.Manifest.Artifacts.SourceRoot)
            : null;
        if (sourceRoot is not null)
        {
            EnsureArtifactIsInsideSourceRoot(dotnetSource, sourceRoot, "dotnet project");
            if (webSource is not null)
                EnsureArtifactIsInsideSourceRoot(webSource, sourceRoot, "web package");
        }
        string dotnetTargetDirectory = targetRoot ?? Path.GetDirectoryName(dotnetTarget)!;
        string? webTargetDirectory = targetRoot ?? (webTarget is null ? null : Path.GetDirectoryName(webTarget)!);
        if (Directory.Exists(dotnetTargetDirectory)
            || (webTargetDirectory is not null
                && !string.Equals(webTargetDirectory, dotnetTargetDirectory, StringComparison.Ordinal)
                && Directory.Exists(webTargetDirectory)))
            return ToReport(modules,
            [
                "Ejection refuses to overwrite an existing source directory. Choose clean artifact paths in the reviewed source bundle."
            ]);
        string? verifiedSourceRoot = sourceRoot is null
            ? null
            : StageVerifiedSourceTree(sourceRoot, source.Manifest.Artifacts.SourceTreeSha256);

        string targetManifestPath = Path.Combine(dotnetTargetDirectory, "try" + "katch.module.json");
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
            if (verifiedSourceRoot is not null)
                CopyDirectory(verifiedSourceRoot, dotnetTargetDirectory);
            else
                CopyDirectory(Path.GetDirectoryName(dotnetSource)!, dotnetTargetDirectory);
            if (sourceRoot is null && webSource is not null && webTargetDirectory is not null)
                CopyDirectory(Path.GetDirectoryName(webSource)!, webTargetDirectory);
            WriteAtomicBytes(targetManifestPath, source.ManifestBytes);

            catalog.Modules.Remove(installed.Registration);
            RemovePackageReferences(installed.Manifest, catalog);
            catalog.Modules.Add(installed.Registration);
            AddProjectReference(
                ResolveHostProject(catalog.Outputs.Backend, catalog.Outputs.BackendNamespace, "API"),
                dotnetTarget);
            AddProjectReference(
                ResolveHostProject(catalog.Outputs.Migrator, catalog.Outputs.MigratorNamespace, "migrator"),
                dotnetTarget);
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
            RestorePackageGraphs(catalog, webTarget is not null);
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
            if (webTargetDirectory is not null
                && !string.Equals(webTargetDirectory, dotnetTargetDirectory, StringComparison.Ordinal)
                && Directory.Exists(webTargetDirectory))
                Directory.Delete(webTargetDirectory, recursive: true);
            throw;
        }
        finally
        {
            if (verifiedSourceRoot is not null && Directory.Exists(verifiedSourceRoot))
                Directory.Delete(verifiedSourceRoot, recursive: true);
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

    private static string SourceDirectoryPath(string bundleRoot, string workspaceRelativePath)
    {
        if (string.IsNullOrWhiteSpace(workspaceRelativePath) || Path.IsPathRooted(workspaceRelativePath))
            throw new InvalidOperationException("Source bundle root must use a workspace-relative path.");
        string path = Path.GetFullPath(workspaceRelativePath, bundleRoot);
        string prefix = Path.TrimEndingDirectorySeparator(bundleRoot) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || !Directory.Exists(path))
            throw new InvalidOperationException("Source bundle root must be an existing directory inside the reviewed bundle.");
        return path;
    }

    private static void EnsureArtifactIsInsideSourceRoot(string artifact, string sourceRoot, string subject)
    {
        string relative = Path.GetRelativePath(sourceRoot, artifact);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException($"Source bundle {subject} must be inside artifacts.sourceRoot.");
    }

    private static string StageVerifiedSourceTree(string sourceRoot, string expectedSha256)
    {
        if (expectedSha256.Length != 64 || expectedSha256.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException("Source bundle must declare a valid artifacts.sourceTreeSha256 digest.");
        if (new DirectoryInfo(sourceRoot).LinkTarget is not null)
            throw new InvalidOperationException("Source bundle root must not be a symbolic link.");
        string stagingRoot = Directory.CreateTempSubdirectory("trykatch-source-").FullName;
        try
        {
            StringBuilder inventory = new();
            foreach ((string file, string relative) in Directory
                         .EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
                         .Select(file => (
                             File: file,
                             Relative: Path.GetRelativePath(sourceRoot, file)
                                 .Replace(Path.DirectorySeparatorChar, '/')))
                         .OrderBy(item => item.Relative, StringComparer.Ordinal))
            {
                for (FileSystemInfo? item = new FileInfo(file); item is not null && item.FullName != sourceRoot;
                     item = item is FileInfo current ? current.Directory : ((DirectoryInfo)item).Parent)
                    if (item.LinkTarget is not null)
                        throw new InvalidOperationException("Source bundle artifacts must not traverse symbolic links.");
                byte[] reviewedBytes = File.ReadAllBytes(file);
                string digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(reviewedBytes))
                    .ToLowerInvariant();
                inventory.Append(relative).Append('\0').Append(digest).Append('\n');
                string stagedFile = Path.Combine(stagingRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(stagedFile)!);
                File.WriteAllBytes(stagedFile, reviewedBytes);
            }
            string actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    Encoding.UTF8.GetBytes(inventory.ToString())))
                .ToLowerInvariant();
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Source bundle tree integrity check failed.");
            return stagingRoot;
        }
        catch
        {
            Directory.Delete(stagingRoot, recursive: true);
            throw;
        }
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
