using FlatpackApp.ModuleTool;
using Shouldly;

namespace FlatpackApp.UnitTests;

[TestClass]
public sealed class ModuleWorkspaceTests
{
    [TestMethod]
    public void GenerateProducesHealthyDeterministicBackendAndWebRegistries()
    {
        using TemporaryModuleWorkspace temporary = TemporaryModuleWorkspace.Create();
        ModuleWorkspace workspace = new(temporary.Root);

        ModuleDoctorReport generated = workspace.Generate();
        ModuleDoctorReport inspected = workspace.Inspect();

        generated.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, generated.Errors));
        inspected.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, inspected.Errors));
        File.ReadAllText(Path.Combine(temporary.Root, "src/GeneratedModules.cs")).ShouldContain("new ProjectsModule()");
        File.ReadAllText(Path.Combine(temporary.Root, "web/src/modules.ts")).ShouldContain("projectsModule");
    }

    [TestMethod]
    public void DoctorDetectsGeneratedRegistryDrift()
    {
        using TemporaryModuleWorkspace temporary = TemporaryModuleWorkspace.Create();
        ModuleWorkspace workspace = new(temporary.Root);
        workspace.Generate().IsHealthy.ShouldBeTrue();
        File.AppendAllText(Path.Combine(temporary.Root, "src/GeneratedModules.cs"), "// drift\n");

        ModuleDoctorReport report = workspace.Inspect();

        report.IsHealthy.ShouldBeFalse();
        report.Errors.ShouldContain(error => error.Contains("has drifted", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DisableRefusesToBreakAnEnabledRequiredDependency()
    {
        using TemporaryModuleWorkspace temporary = TemporaryModuleWorkspace.Create(includeDependent: true);
        ModuleWorkspace workspace = new(temporary.Root);
        workspace.Generate().IsHealthy.ShouldBeTrue();
        string before = File.ReadAllText(Path.Combine(temporary.Root, "flatpack.modules.json"));

        ModuleDoctorReport report = workspace.SetEnabled("projects", enabled: false);

        report.IsHealthy.ShouldBeFalse();
        report.Errors.ShouldContain(error => error.Contains("requires disabled module 'projects'", StringComparison.Ordinal));
        File.ReadAllText(Path.Combine(temporary.Root, "flatpack.modules.json")).ShouldBe(before);
    }

    [TestMethod]
    public void DisableRemovesLeafModuleFromBothRegistriesWithoutDeletingItsManifest()
    {
        using TemporaryModuleWorkspace temporary = TemporaryModuleWorkspace.Create(includeDependent: true);
        ModuleWorkspace workspace = new(temporary.Root);
        workspace.Generate().IsHealthy.ShouldBeTrue();

        ModuleDoctorReport report = workspace.SetEnabled("getting-started", enabled: false);

        report.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, report.Errors));
        File.ReadAllText(Path.Combine(temporary.Root, "src/GeneratedModules.cs")).ShouldNotContain("GettingStartedModule");
        File.ReadAllText(Path.Combine(temporary.Root, "web/src/modules.ts")).ShouldNotContain("gettingStartedModule");
        File.Exists(Path.Combine(temporary.Root, "manifests/getting-started.json")).ShouldBeTrue();
        workspace.Inspect().IsHealthy.ShouldBeTrue();
    }

    [TestMethod]
    public void DoctorRejectsContributionCollisionsAcrossEnabledModules()
    {
        using TemporaryModuleWorkspace temporary = TemporaryModuleWorkspace.Create(includeDependent: true, duplicateRoute: true);

        ModuleDoctorReport report = new ModuleWorkspace(temporary.Root).Generate();

        report.IsHealthy.ShouldBeFalse();
        report.Errors.ShouldContain(error => error.Contains("Duplicate route path across enabled modules '/projects'", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DoctorReportsDuplicateRegistrationsWithoutCrashing()
    {
        using TemporaryModuleWorkspace temporary = TemporaryModuleWorkspace.Create(duplicateRegistration: true);

        ModuleDoctorReport report = new ModuleWorkspace(temporary.Root).Inspect();

        report.IsHealthy.ShouldBeFalse();
        report.Errors.ShouldContain(error => error.Contains("Duplicate module registration id 'projects'", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DoctorRejectsUnknownCatalogProperties()
    {
        using TemporaryModuleWorkspace temporary = TemporaryModuleWorkspace.Create();
        string catalogPath = Path.Combine(temporary.Root, "flatpack.modules.json");
        string catalog = File.ReadAllText(catalogPath).Replace(
            "\"hostVersion\": \"0.1.0\",",
            "\"hostVersion\": \"0.1.0\",\n  \"unexpected\": true,",
            StringComparison.Ordinal);
        File.WriteAllText(catalogPath, catalog);

        ModuleDoctorReport report = new ModuleWorkspace(temporary.Root).Inspect();

        report.IsHealthy.ShouldBeFalse();
        report.Errors.ShouldContain(error => error.Contains("could not be mapped", StringComparison.Ordinal));
    }

    private sealed class TemporaryModuleWorkspace : IDisposable
    {
        private TemporaryModuleWorkspace(string root) => Root = root;

        public string Root { get; }

        public static TemporaryModuleWorkspace Create(
            bool includeDependent = false,
            bool duplicateRoute = false,
            bool duplicateRegistration = false)
        {
            string root = Path.Combine(Path.GetTempPath(), $"flatpack-module-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(root, "manifests"));
            Directory.CreateDirectory(Path.Combine(root, "src"));
            Directory.CreateDirectory(Path.Combine(root, "web/src"));
            Directory.CreateDirectory(Path.Combine(root, "web/packages/projects"));
            Directory.CreateDirectory(Path.Combine(root, "web/packages/getting-started"));
            File.WriteAllText(Path.Combine(root, "src/Projects.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(root, "src/GettingStarted.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(root, "web/packages/projects/package.json"), "{}");
            File.WriteAllText(Path.Combine(root, "web/packages/getting-started/package.json"), "{}");

            File.WriteAllText(Path.Combine(root, "manifests/projects.json"), Manifest(
                "projects",
                "FlatpackApp.Modules.Projects.ProjectsModule",
                "./projects",
                "projectsModule",
                [],
                "/projects"));
            if (includeDependent)
            {
                File.WriteAllText(Path.Combine(root, "manifests/getting-started.json"), Manifest(
                    "getting-started",
                    "FlatpackApp.Modules.GettingStarted.GettingStartedModule",
                    "./getting-started",
                    "gettingStartedModule",
                    ["projects"],
                    duplicateRoute ? "/projects" : "/getting-started"));
            }

            string registrations = duplicateRegistration
                ? """
                    { "id": "projects", "manifest": "manifests/projects.json", "enabled": true },
                    { "id": "projects", "manifest": "manifests/projects.json", "enabled": true }
                  """
                : includeDependent
                ? """
                    { "id": "projects", "manifest": "manifests/projects.json", "enabled": true },
                    { "id": "getting-started", "manifest": "manifests/getting-started.json", "enabled": true }
                  """
                : """{ "id": "projects", "manifest": "manifests/projects.json", "enabled": true }""";
            File.WriteAllText(Path.Combine(root, "flatpack.modules.json"), $$"""
                {
                  "schemaVersion": 1,
                  "hostVersion": "0.1.0",
                  "outputs": {
                    "backend": "src/GeneratedModules.cs",
                    "backendNamespace": "FlatpackApp.Api.Modules",
                    "web": "web/src/modules.ts"
                  },
                  "modules": [
                    {{registrations}}
                  ]
                }
                """);
            return new(root);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }

        private static string Manifest(
            string id,
            string dotnetType,
            string webSpecifier,
            string webExport,
            IReadOnlyList<string> requires,
            string route) => $$"""
                {
                  "schemaVersion": 1,
                  "id": "{{id}}",
                  "name": "{{id}}",
                  "version": "1.0.0",
                  "description": "Test module.",
                  "compatibility": {
                    "minimumHostVersion": "0.1.0",
                    "maximumHostVersionExclusive": "1.0.0"
                  },
                  "requires": [{{string.Join(", ", requires.Select(value => $"\"{value}\""))}}],
                  "optionalDependencies": [],
                  "capabilities": ["api", "web"],
                  "artifacts": {
                    "dotnetProject": "src/{{(id == "projects" ? "Projects" : "GettingStarted")}}.csproj",
                    "webPackage": "web/packages/{{id}}/package.json"
                  },
                  "entrypoints": {
                    "dotnet": { "type": "{{dotnetType}}" },
                    "web": { "specifier": "{{webSpecifier}}", "export": "{{webExport}}" }
                  },
                  "contributions": {
                    "permissions": ["{{id}}.read"],
                    "routes": [{ "id": "{{id}}.home", "path": "{{route}}" }],
                    "extensionPoints": [],
                    "extensions": [],
                    "assistantTools": []
                  }
                }
                """;
    }
}
