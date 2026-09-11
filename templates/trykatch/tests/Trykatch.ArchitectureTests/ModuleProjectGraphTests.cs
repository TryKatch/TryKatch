using System.Text.Json;
using System.Xml.Linq;
using Shouldly;

namespace Trykatch.ArchitectureTests;

[TestClass]
public sealed class ModuleProjectGraphTests
{
    private static readonly string Workspace = FindWorkspace();
    private static readonly string[] LayerNames = ["Domain", "Application", "IntegrationEvents", "Presentation", "Infrastructure"];

    [TestMethod]
    public void EveryDiscoveredBusinessModuleHasTheRequiredFiveProjectLayers()
    {
        string[] modules = DiscoverModules(Workspace);
        modules.ShouldNotBeEmpty();
        foreach (string module in modules)
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
    public void ModuleProjectReferencesObeyTheCompleteLayerContract()
    {
        string[] errors = ModuleProjectGraphInspector.Inspect(Workspace);
        errors.ShouldBeEmpty(string.Join(Environment.NewLine, errors));
    }

    [TestMethod]
    public void DeliberatelyInvalidProjectReferenceIsRejected()
    {
        string root = Path.Combine(Path.GetTempPath(), $"trykatch-architecture-{Guid.NewGuid():N}");
        try
        {
            foreach (string module in new[] { "Alpha", "Beta" })
            {
                string moduleRoot = Path.Combine(root, "src", "Modules", module);
                Directory.CreateDirectory(moduleRoot);
                File.WriteAllText(Path.Combine(moduleRoot, "trykatch.module.json"), "{}");
                foreach (string layer in LayerNames)
                {
                    string projectRoot = Path.Combine(moduleRoot, $"Trykatch.Modules.{module}.{layer}");
                    Directory.CreateDirectory(projectRoot);
                    File.WriteAllText(
                        Path.Combine(projectRoot, $"Trykatch.Modules.{module}.{layer}.csproj"),
                        "<Project Sdk=\"Microsoft.NET.Sdk\" />");
                }
            }

            string alphaApplication = Path.Combine(
                root, "src", "Modules", "Alpha", "Trykatch.Modules.Alpha.Application",
                "Trykatch.Modules.Alpha.Application.csproj");
            File.WriteAllText(alphaApplication, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <ProjectReference Include="../../Beta/Trykatch.Modules.Beta.Infrastructure/Trykatch.Modules.Beta.Infrastructure.csproj" />
                  </ItemGroup>
                </Project>
                """);

            string[] errors = ModuleProjectGraphInspector.Inspect(root);

            errors.ShouldContain(error => error.Contains(
                "Alpha.Application cannot reference Beta.Infrastructure",
                StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ModuleManifestIdsAreUniqueStableAndAlignedWithEntrypoints()
    {
        string[] manifests = Directory.GetFiles(
            Path.Combine(Workspace, "src", "Modules"),
            "trykatch.module.json",
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
        ids.Count.ShouldBe(DiscoverModules(Workspace).Length);
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

    private static string[] DiscoverModules(string root) => Directory
        .EnumerateFiles(Path.Combine(root, "src", "Modules"), "trykatch.module.json", SearchOption.AllDirectories)
        .Select(path => Directory.GetParent(path)!.Name)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static string FindWorkspace()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Trykatch.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Could not locate Trykatch workspace root.");
    }
}

internal static class ModuleProjectGraphInspector
{
    private static readonly HashSet<string> Layers =
        ["Domain", "Application", "IntegrationEvents", "Presentation", "Infrastructure"];

    private static readonly Dictionary<string, HashSet<string>> AllowedOwnModuleLayers =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["Domain"] = [],
            ["Application"] = ["Domain", "IntegrationEvents"],
            ["IntegrationEvents"] = [],
            ["Presentation"] = ["Application"],
            ["Infrastructure"] = ["Domain", "Application", "IntegrationEvents", "Presentation"]
        };

    private static readonly Dictionary<string, HashSet<string>> AllowedCommonProjects =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["Domain"] = ["Trykatch.Domain", "Trykatch.Modules.Abstractions"],
            ["Application"] = ["Trykatch.Application", "Trykatch.Modules.Abstractions"],
            ["IntegrationEvents"] = ["Trykatch.Modules.Abstractions"],
            ["Presentation"] = ["Trykatch.Application", "Trykatch.Modules.AspNetCore"],
            ["Infrastructure"] =
            [
                "Trykatch.Application", "Trykatch.Domain", "Trykatch.Identity", "Trykatch.Infrastructure",
                "Trykatch.Modules.Abstractions", "Trykatch.Modules.AspNetCore", "Trykatch.ServiceDefaults"
            ]
        };

    public static string[] Inspect(string workspace)
    {
        List<string> errors = [];
        string modulesRoot = Path.Combine(workspace, "src", "Modules");
        if (!Directory.Exists(modulesRoot)) return ["The src/Modules directory is missing."];

        string[] modules = Directory
            .EnumerateFiles(modulesRoot, "trykatch.module.json", SearchOption.AllDirectories)
            .Select(path => Directory.GetParent(path)!.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (string module in modules)
        foreach (string project in Directory.EnumerateFiles(
                     Path.Combine(modulesRoot, module), "*.csproj", SearchOption.AllDirectories))
        {
            string layer = Layers.SingleOrDefault(candidate =>
                    Path.GetFileNameWithoutExtension(project).EndsWith($".{candidate}", StringComparison.Ordinal))
                ?? string.Empty;
            if (layer.Length == 0)
            {
                errors.Add($"Unknown project layer: {project}");
                continue;
            }

            XDocument document = XDocument.Load(project);
            foreach (XElement reference in document.Descendants("ProjectReference"))
            {
                string? include = reference.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include))
                {
                    errors.Add($"{module}.{layer} has an empty ProjectReference.");
                    continue;
                }

                string target = Path.GetFullPath(include, Path.GetDirectoryName(project)!);
                string targetName = Path.GetFileNameWithoutExtension(target);
                string normalized = target.Replace('\\', '/');
                if (TryReadModuleTarget(normalized, out string targetModule, out string targetLayer))
                {
                    bool allowed = targetModule == module
                        ? AllowedOwnModuleLayers[layer].Contains(targetLayer)
                        : targetLayer == "IntegrationEvents";
                    if (!allowed)
                        errors.Add($"{module}.{layer} cannot reference {targetModule}.{targetLayer} ({targetName}).");
                    continue;
                }

                if (normalized.Contains("/src/Common/", StringComparison.Ordinal))
                {
                    if (!AllowedCommonProjects[layer].Contains(targetName))
                        errors.Add($"{module}.{layer} cannot reference Common project {targetName}.");
                    continue;
                }

                errors.Add($"{module}.{layer} cannot reference project outside its module or approved Common seams: {targetName}.");
            }

            if (layer is "Domain" or "Application" or "IntegrationEvents")
            {
                foreach (string framework in document.Descendants("FrameworkReference")
                             .Select(item => (string?)item.Attribute("Include"))
                             .OfType<string>())
                    errors.Add($"{module}.{layer} cannot reference framework {framework}.");
            }

            if (layer is "Domain" or "IntegrationEvents")
            {
                foreach (string package in document.Descendants("PackageReference")
                             .Select(item => (string?)item.Attribute("Include"))
                             .OfType<string>())
                    errors.Add($"{module}.{layer} cannot reference package {package}.");
            }
            else if (layer == "Application")
            {
                string[] forbiddenPrefixes =
                    ["Microsoft.AspNetCore", "Microsoft.EntityFrameworkCore", "Microsoft.Extensions.Http", "Npgsql"];
                foreach (string package in document.Descendants("PackageReference")
                             .Select(item => (string?)item.Attribute("Include"))
                             .OfType<string>())
                    if (forbiddenPrefixes.Any(prefix => package.StartsWith(prefix, StringComparison.Ordinal)))
                        errors.Add($"{module}.Application cannot reference adapter package {package}.");
            }
        }

        string apiRoot = Path.Combine(workspace, "src", "API");
        if (Directory.Exists(apiRoot))
        foreach (string project in Directory.EnumerateFiles(apiRoot, "*.csproj", SearchOption.AllDirectories))
        foreach (XElement reference in XDocument.Load(project).Descendants("ProjectReference"))
        {
            string target = Path.GetFullPath(reference.Attribute("Include")?.Value ?? string.Empty, Path.GetDirectoryName(project)!);
            string normalized = target.Replace('\\', '/');
            if (TryReadModuleTarget(normalized, out string targetModule, out string targetLayer)
                && targetLayer != "Infrastructure")
                errors.Add($"Host {Path.GetFileNameWithoutExtension(project)} cannot reference {targetModule}.{targetLayer}.");
        }

        return errors.ToArray();
    }

    private static bool TryReadModuleTarget(string normalizedPath, out string module, out string layer)
    {
        string marker = "/src/Modules/";
        int start = normalizedPath.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            module = string.Empty;
            layer = string.Empty;
            return false;
        }

        string[] segments = normalizedPath[(start + marker.Length)..].Split('/');
        module = segments.ElementAtOrDefault(0) ?? string.Empty;
        string project = segments.ElementAtOrDefault(1) ?? string.Empty;
        layer = Layers.SingleOrDefault(candidate => project.EndsWith($".{candidate}", StringComparison.Ordinal))
            ?? string.Empty;
        return module.Length > 0 && layer.Length > 0;
    }
}
