using System.Text.Json;
using System.Xml.Linq;
using Shouldly;

namespace Trykatch.ArchitectureTests;

[TestClass]
public sealed class ModuleProjectGraphTests
{
    private static readonly string Workspace = FindWorkspace();
    private static readonly string[] ModuleNames = ["Projects", "Documents", "Federation"];
    private static readonly string[] LayerNames = ["Domain", "Application", "IntegrationEvents", "Presentation", "Infrastructure"];

    [TestMethod]
    public void EveryBusinessModuleHasTheRequiredFiveProjectLayers()
    {
        foreach (string module in ModuleNames)
        foreach (string layer in LayerNames)
        {
            string project = Path.Combine(
                Workspace,
                "src", "Modules", module,
                $"Trykatch.Modules.{module}.{layer}",
                $"Trykatch.Modules.{module}.{layer}.csproj");
            File.Exists(project).ShouldBeTrue($"Missing {module} {layer} project: {project}");
        }
    }

    [TestMethod]
    public void ModuleProjectReferencesObeyTheLayerAndCrossModuleBoundaries()
    {
        foreach (string module in ModuleNames)
        foreach (string project in Directory.GetFiles(
                     Path.Combine(Workspace, "src", "Modules", module),
                     "*.csproj",
                     SearchOption.AllDirectories))
        {
            string layer = LayerNames.Single(candidate => project.Contains($".{candidate}{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
            string[] references = XDocument.Load(project)
                .Descendants("ProjectReference")
                .Select(reference => Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(project)!,
                    reference.Attribute("Include")?.Value ?? string.Empty)).Replace('\\', '/'))
                .ToArray();

            references.ShouldNotContain(reference => ModuleNames
                .Where(other => other != module)
                .Any(other => reference.Contains($"/src/Modules/{other}/", StringComparison.Ordinal)
                    && !reference.Contains(".IntegrationEvents/", StringComparison.Ordinal)));

            if (layer == "Domain" || layer == "IntegrationEvents")
                references.ShouldNotContain(reference => reference.Contains($"Trykatch.Modules.{module}.", StringComparison.Ordinal));
            if (layer == "Application")
                references.ShouldNotContain(reference => reference.Contains(".Presentation/", StringComparison.Ordinal)
                    || reference.Contains(".Infrastructure/", StringComparison.Ordinal));
            if (layer == "Presentation")
                references.ShouldNotContain(reference => reference.Contains(".Infrastructure/", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void HostReferencesOnlyModuleInfrastructureProjects()
    {
        string[] hostProjects = Directory.GetFiles(Path.Combine(Workspace, "src", "API"), "*.csproj", SearchOption.AllDirectories);
        foreach (string project in hostProjects)
        {
            string[] moduleReferences = XDocument.Load(project)
                .Descendants("ProjectReference")
                .Select(reference => Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(project)!,
                    reference.Attribute("Include")?.Value ?? string.Empty)).Replace('\\', '/'))
                .Where(reference => reference.Contains("/src/Modules/", StringComparison.Ordinal))
                .ToArray();
            moduleReferences.ShouldAllBe(reference => reference.Contains(".Infrastructure/", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void ModuleManifestIdsAreUniqueStableAndAlignedWithEntrypoints()
    {
        string[] manifests = Directory.GetFiles(
            Path.Combine(Workspace, "src", "Modules"),
            "try" + "katch.module.json",
            SearchOption.AllDirectories);
        List<string> ids = [];
        foreach (string path in manifests)
        {
            using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(path));
            string id = manifest.RootElement.GetProperty("id").GetString()!;
            string module = Directory.GetParent(path)!.Name;
            string entrypoint = manifest.RootElement.GetProperty("entrypoints").GetProperty("dotnet").GetProperty("type").GetString()!;
            id.ShouldMatch("^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$");
            entrypoint.ShouldBe($"Trykatch.Modules.{module}.Infrastructure.{module}Module");
            ids.Add(id);
        }
        ids.Count.ShouldBe(ModuleNames.Length);
        ids.Distinct(StringComparer.Ordinal).Count().ShouldBe(ids.Count);
    }

    [TestMethod]
    public void ModuleCompositionDoesNotUseRuntimeAssemblyScanning()
    {
        string[] forbiddenPatterns =
        [
            "AppDomain.CurrentDomain.GetAssemblies",
            "Assembly.Load(",
            "Assembly.LoadFile(",
            "Assembly.LoadFrom(",
            ".GetExportedTypes(",
            ".GetTypes()"
        ];
        string[] sourceFiles = Directory.GetFiles(
            Path.Combine(Workspace, "src"),
            "*.cs",
            SearchOption.AllDirectories);

        foreach (string sourceFile in sourceFiles)
        {
            string source = File.ReadAllText(sourceFile);
            foreach (string pattern in forbiddenPatterns)
                source.Contains(pattern, StringComparison.Ordinal).ShouldBeFalse(
                    $"Runtime module discovery is forbidden; found '{pattern}' in {sourceFile}.");
        }
    }

    private static string FindWorkspace()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Trykatch.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Could not locate Trykatch workspace root.");
    }
}
