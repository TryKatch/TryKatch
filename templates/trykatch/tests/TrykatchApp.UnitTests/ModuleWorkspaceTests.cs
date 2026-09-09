using TrykatchApp.ModuleTool;
using Shouldly;
using System.Security.Cryptography;

namespace TrykatchApp.UnitTests;

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
        File.ReadAllText(Path.Combine(temporary.Root, "src/GeneratedMigratorModules.cs")).ShouldContain("new ProjectsModule()");
        File.ReadAllText(Path.Combine(temporary.Root, "web/src/modules.ts")).ShouldContain("projectsModule");
        File.ReadAllText(Path.Combine(temporary.Root, "trykatch.modules.lock.json")).ShouldContain("manifestSha256");
    }

    [TestMethod]
    public void WorkspaceManifestDigestSurvivesDotnetTemplateNameReplacement()
    {
        using TemporaryModuleWorkspace temporary = TemporaryModuleWorkspace.Create();
        ModuleWorkspace workspace = new(temporary.Root);
        workspace.Generate().IsHealthy.ShouldBeTrue();
        string lockBeforeRename = File.ReadAllText(Path.Combine(temporary.Root, "trykatch.modules.lock.json"));

        string catalogPath = Path.Combine(temporary.Root, "trykatch.modules.json");
        string manifestPath = Path.Combine(temporary.Root, "manifests/projects.json");
        File.WriteAllText(
            catalogPath,
            File.ReadAllText(catalogPath)
                .Replace("TrykatchApp", "Horizon", StringComparison.Ordinal)
                .Replace("trykatchapp", "horizon", StringComparison.Ordinal));
        File.WriteAllText(
            manifestPath,
            File.ReadAllText(manifestPath)
                .Replace("TrykatchApp", "Horizon", StringComparison.Ordinal)
                .Replace("trykatchapp", "horizon", StringComparison.Ordinal));

        ModuleDoctorReport renamed = new ModuleWorkspace(temporary.Root).Generate();

        renamed.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, renamed.Errors));
        File.ReadAllText(Path.Combine(temporary.Root, "src/GeneratedModules.cs"))
            .ShouldContain("using Horizon.Modules;");
        File.ReadAllText(Path.Combine(temporary.Root, "web/src/modules.ts"))
            .ShouldContain("from '@horizon/module-sdk'");
        File.ReadAllText(Path.Combine(temporary.Root, "trykatch.modules.lock.json")).ShouldBe(lockBeforeRename);
        lockBeforeRename.ShouldContain("template-normalized-sha256");
    }

    [TestMethod]
    public void LegacyVersionOneCatalogDerivesPortableOutputIdentities()
    {
        using TemporaryModuleWorkspace temporary = TemporaryModuleWorkspace.Create();
        string catalogPath = Path.Combine(temporary.Root, "trykatch.modules.json");
        string catalog = File.ReadAllText(catalogPath)
            .Replace("    \"dotnetModuleContractNamespace\": \"TrykatchApp.Modules\",\n", string.Empty, StringComparison.Ordinal)
            .Replace("    \"web\": \"web/src/modules.ts\",\n", "    \"web\": \"web/src/modules.ts\"\n", StringComparison.Ordinal)
            .Replace("    \"webModuleSdkSpecifier\": \"@trykatchapp/module-sdk\"\n", string.Empty, StringComparison.Ordinal)
            .Replace("TrykatchApp", "CustomerPortal", StringComparison.Ordinal)
            .Replace("trykatchapp", "customerportal", StringComparison.Ordinal);
        File.WriteAllText(catalogPath, catalog);
        string manifestPath = Path.Combine(temporary.Root, "manifests/projects.json");
        File.WriteAllText(
            manifestPath,
            File.ReadAllText(manifestPath)
                .Replace("TrykatchApp", "CustomerPortal", StringComparison.Ordinal)
                .Replace("trykatchapp", "customerportal", StringComparison.Ordinal));

        ModuleWorkspace workspace = new(temporary.Root);
        ModuleDoctorReport generated = workspace.Generate();

        generated.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, generated.Errors));
        File.ReadAllText(Path.Combine(temporary.Root, "src/GeneratedModules.cs"))
            .ShouldContain("using CustomerPortal.Modules;");
        File.ReadAllText(Path.Combine(temporary.Root, "web/src/modules.ts"))
            .ShouldContain("from '@customerportal/module-sdk'");
        workspace.Inspect().IsHealthy.ShouldBeTrue();
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
        string before = File.ReadAllText(Path.Combine(temporary.Root, "trykatch.modules.json"));

        ModuleDoctorReport report = workspace.SetEnabled("projects", enabled: false);

        report.IsHealthy.ShouldBeFalse();
        report.Errors.ShouldContain(error => error.Contains("requires disabled module 'projects'", StringComparison.Ordinal));
        File.ReadAllText(Path.Combine(temporary.Root, "trykatch.modules.json")).ShouldBe(before);
    }

    [TestMethod]
    public void DisableRemovesLeafModuleFromBothRegistriesWithoutDeletingItsManifest()
    {
        using TemporaryModuleWorkspace temporary = TemporaryModuleWorkspace.Create(includeDependent: true);
        ModuleWorkspace workspace = new(temporary.Root);
        workspace.Generate().IsHealthy.ShouldBeTrue();

        ModuleDoctorReport report = workspace.SetEnabled("sample-extension", enabled: false);

        report.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, report.Errors));
        File.ReadAllText(Path.Combine(temporary.Root, "src/GeneratedModules.cs")).ShouldNotContain("SampleExtensionModule");
        File.ReadAllText(Path.Combine(temporary.Root, "web/src/modules.ts")).ShouldNotContain("sampleExtensionModule");
        File.Exists(Path.Combine(temporary.Root, "manifests/sample-extension.json")).ShouldBeTrue();
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
        string catalogPath = Path.Combine(temporary.Root, "trykatch.modules.json");
        string catalog = File.ReadAllText(catalogPath).Replace(
            "\"hostVersion\": \"0.1.0\",",
            "\"hostVersion\": \"0.1.0\",\n  \"unexpected\": true,",
            StringComparison.Ordinal);
        File.WriteAllText(catalogPath, catalog);

        ModuleDoctorReport report = new ModuleWorkspace(temporary.Root).Inspect();

        report.IsHealthy.ShouldBeFalse();
        report.Errors.ShouldContain(error => error.Contains("could not be mapped", StringComparison.Ordinal));
    }

    [TestMethod]
    public void InstallAndUnregisterPackageMutateBothPackageGraphsAndRetainSourceModules()
    {
        using TemporaryModuleWorkspace temporary = TemporaryModuleWorkspace.Create();
        RecordingCommandRunner runner = new();
        ModuleWorkspace workspace = new(temporary.Root, runner);
        workspace.Generate().IsHealthy.ShouldBeTrue();
        string candidatePath = temporary.WritePackageManifest("reporting", "1.0.0");
        string digest = Sha256(candidatePath);

        ModuleDoctorReport installed = workspace.InstallPackage(candidatePath, digest);

        installed.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, installed.Errors));
        installed.Modules.Single(module => module.Id == "reporting").Enabled.ShouldBeFalse();
        File.ReadAllText(Path.Combine(temporary.Root, "Directory.Packages.props"))
            .ShouldContain("Trykatch.Modules.Reporting");
        File.ReadAllText(Path.Combine(temporary.Root, "web/apps/web/package.json"))
            .ShouldContain("@trykatchapp/module-reporting");
        File.ReadAllText(Path.Combine(temporary.Root, "trykatch.modules.lock.json"))
            .ShouldContain(digest);
        runner.Commands.Count.ShouldBe(2);

        ModuleDoctorReport removed = workspace.Unregister("reporting");

        removed.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, removed.Errors));
        removed.Modules.ShouldNotContain(module => module.Id == "reporting");
        File.ReadAllText(Path.Combine(temporary.Root, "Directory.Packages.props"))
            .ShouldNotContain("Trykatch.Modules.Reporting");
        File.Exists(Path.Combine(temporary.Root, "manifests/projects.json")).ShouldBeTrue();
        runner.Commands.Count.ShouldBe(4);
    }

    [TestMethod]
    public void InstallRollsBackEveryWorkspaceFileWhenPackageRestoreFails()
    {
        using TemporaryModuleWorkspace temporary = TemporaryModuleWorkspace.Create();
        ModuleWorkspace baseline = new(temporary.Root);
        baseline.Generate().IsHealthy.ShouldBeTrue();
        string catalogBefore = File.ReadAllText(Path.Combine(temporary.Root, "trykatch.modules.json"));
        string packagesBefore = File.ReadAllText(Path.Combine(temporary.Root, "Directory.Packages.props"));
        string candidatePath = temporary.WritePackageManifest("reporting", "1.0.0");
        RecordingCommandRunner runner = new(failFirstCommand: true);

        Should.Throw<InvalidOperationException>(() =>
            new ModuleWorkspace(temporary.Root, runner).InstallPackage(candidatePath, Sha256(candidatePath)));

        File.ReadAllText(Path.Combine(temporary.Root, "trykatch.modules.json")).ShouldBe(catalogBefore);
        File.ReadAllText(Path.Combine(temporary.Root, "Directory.Packages.props")).ShouldBe(packagesBefore);
        Directory.Exists(Path.Combine(temporary.Root, ".trykatch/modules/reporting")).ShouldBeTrue();
        Directory.EnumerateFiles(Path.Combine(temporary.Root, ".trykatch/modules/reporting"), "*", SearchOption.AllDirectories)
            .ShouldBeEmpty();
        new ModuleWorkspace(temporary.Root).Inspect().IsHealthy.ShouldBeTrue();
    }

    [TestMethod]
    public void UpgradeRequiresAForwardVersionAndKeepsPackageIdentityStable()
    {
        using TemporaryModuleWorkspace temporary = TemporaryModuleWorkspace.Create();
        RecordingCommandRunner runner = new();
        ModuleWorkspace workspace = new(temporary.Root, runner);
        workspace.Generate().IsHealthy.ShouldBeTrue();
        string versionOne = temporary.WritePackageManifest("reporting", "1.0.0");
        workspace.InstallPackage(versionOne, Sha256(versionOne)).IsHealthy.ShouldBeTrue();
        string versionTwo = temporary.WritePackageManifest("reporting", "1.1.0");

        ModuleDoctorReport upgraded = workspace.UpgradePackage(versionTwo, Sha256(versionTwo));

        upgraded.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, upgraded.Errors));
        upgraded.Modules.Single(module => module.Id == "reporting").Version.ShouldBe("1.1.0");
        File.ReadAllText(Path.Combine(temporary.Root, "Directory.Packages.props"))
            .ShouldContain("Version=\"1.1.0\"");
        File.ReadAllText(Path.Combine(temporary.Root, "trykatch.modules.lock.json"))
            .ShouldContain("\"version\": \"1.1.0\"");
    }

    [TestMethod]
    public void InstallRejectsManifestWhenExpectedDigestDoesNotMatch()
    {
        using TemporaryModuleWorkspace temporary = TemporaryModuleWorkspace.Create();
        string candidate = temporary.WritePackageManifest("reporting", "1.0.0");

        Should.Throw<InvalidOperationException>(() =>
                new ModuleWorkspace(temporary.Root).InstallPackage(candidate, new string('0', 64)))
            .Message.ShouldContain("integrity check failed");
    }

    [TestMethod]
    public void EjectRequiresDisabledPackageAndSwitchesBothSurfacesToReviewedSource()
    {
        using TemporaryModuleWorkspace temporary = TemporaryModuleWorkspace.Create();
        RecordingCommandRunner runner = new();
        ModuleWorkspace workspace = new(temporary.Root, runner);
        workspace.Generate().IsHealthy.ShouldBeTrue();
        string packageManifest = temporary.WritePackageManifest("reporting", "1.0.0");
        workspace.InstallPackage(packageManifest, Sha256(packageManifest)).IsHealthy.ShouldBeTrue();
        string bundle = temporary.WriteSourceBundle("reporting", "1.0.0");
        string bundleManifest = Path.Combine(bundle, "trykatch.module.json");

        ModuleDoctorReport ejected = workspace.EjectPackage("reporting", bundle, Sha256(bundleManifest));

        ejected.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, ejected.Errors));
        string dotnetProject = Path.Combine(temporary.Root, "src/TrykatchApp.Modules.Reporting/Reporting.csproj");
        File.Exists(dotnetProject).ShouldBeTrue();
        File.ReadAllText(Path.Combine(temporary.Root, "src/TrykatchApp.Api/TrykatchApp.Api.csproj"))
            .ShouldContain("ProjectReference");
        File.ReadAllText(Path.Combine(temporary.Root, "web/apps/web/package.json"))
            .ShouldContain("workspace:*");
        File.ReadAllText(Path.Combine(temporary.Root, "trykatch.modules.lock.json"))
            .ShouldContain("\"kind\": \"workspace\"");
    }

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private sealed class TemporaryModuleWorkspace : IDisposable
    {
        private TemporaryModuleWorkspace(string root) => Root = root;

        public string Root { get; }

        public static TemporaryModuleWorkspace Create(
            bool includeDependent = false,
            bool duplicateRoute = false,
            bool duplicateRegistration = false)
        {
            string root = Path.Combine(Path.GetTempPath(), $"trykatch-module-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(root, "manifests"));
            Directory.CreateDirectory(Path.Combine(root, "src"));
            Directory.CreateDirectory(Path.Combine(root, "web/src"));
            Directory.CreateDirectory(Path.Combine(root, "web/packages/projects"));
            Directory.CreateDirectory(Path.Combine(root, "web/packages/sample-extension"));
            Directory.CreateDirectory(Path.Combine(root, "src/TrykatchApp.Api"));
            Directory.CreateDirectory(Path.Combine(root, "src/TrykatchApp.Migrator"));
            Directory.CreateDirectory(Path.Combine(root, "web/apps/web"));
            File.WriteAllText(Path.Combine(root, "TrykatchApp.slnx"), "<Solution />");
            File.WriteAllText(Path.Combine(root, "src/Projects.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(root, "src/SampleExtension.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(root, "web/packages/projects/package.json"), "{}");
            File.WriteAllText(Path.Combine(root, "web/packages/sample-extension/package.json"), "{}");
            File.WriteAllText(Path.Combine(root, "Directory.Packages.props"),
                "<Project><ItemGroup><PackageVersion Include=\"Existing.Package\" Version=\"1.0.0\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(root, "src/TrykatchApp.Api/TrykatchApp.Api.csproj"),
                "<Project><ItemGroup><PackageReference Include=\"Existing.Package\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(root, "src/TrykatchApp.Migrator/TrykatchApp.Migrator.csproj"),
                "<Project><ItemGroup><PackageReference Include=\"Existing.Package\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(root, "web/apps/web/package.json"),
                "{\n  \"dependencies\": {}\n}\n");
            File.WriteAllText(Path.Combine(root, "web/pnpm-lock.yaml"), "lockfileVersion: '9.0'\n");

            File.WriteAllText(Path.Combine(root, "manifests/projects.json"), Manifest(
                "projects",
                "TrykatchApp.Modules.Projects.ProjectsModule",
                "./projects",
                "projectsModule",
                [],
                "/projects"));
            if (includeDependent)
            {
                File.WriteAllText(Path.Combine(root, "manifests/sample-extension.json"), Manifest(
                    "sample-extension",
                    "TrykatchApp.Modules.SampleExtension.SampleExtensionModule",
                    "./sample-extension",
                    "sampleExtensionModule",
                    ["projects"],
                    duplicateRoute ? "/projects" : "/sample-extension"));
            }

            string registrations = duplicateRegistration
                ? """
                    { "id": "projects", "manifest": "manifests/projects.json", "enabled": true },
                    { "id": "projects", "manifest": "manifests/projects.json", "enabled": true }
                  """
                : includeDependent
                ? """
                    { "id": "projects", "manifest": "manifests/projects.json", "enabled": true },
                    { "id": "sample-extension", "manifest": "manifests/sample-extension.json", "enabled": true }
                  """
                : """{ "id": "projects", "manifest": "manifests/projects.json", "enabled": true }""";
            File.WriteAllText(Path.Combine(root, "trykatch.modules.json"), $$"""
                {
                  "schemaVersion": 1,
                  "hostVersion": "0.1.0",
                  "lockFile": "trykatch.modules.lock.json",
                  "outputs": {
                    "backend": "src/GeneratedModules.cs",
                    "backendNamespace": "TrykatchApp.Api.Modules",
                    "dotnetModuleContractNamespace": "TrykatchApp.Modules",
                    "migrator": "src/GeneratedMigratorModules.cs",
                    "migratorNamespace": "TrykatchApp.Migrator.Modules",
                    "web": "web/src/modules.ts",
                    "webModuleSdkSpecifier": "@trykatchapp/module-sdk"
                  },
                  "modules": [
                    {{registrations}}
                  ]
                }
                """);
            return new(root);
        }

        public string WritePackageManifest(string id, string version)
        {
            string path = Path.Combine(Root, $"{id}-{version}.package.json");
            File.WriteAllText(path, $$"""
                {
                  "schemaVersion": 1,
                  "id": "{{id}}",
                  "name": "Reporting",
                  "version": "{{version}}",
                  "description": "Packaged reporting module.",
                  "distribution": {
                    "kind": "package",
                    "license": "Apache-2.0",
                    "dotnet": { "id": "Trykatch.Modules.Reporting", "version": "{{version}}" },
                    "web": { "id": "@trykatchapp/module-reporting", "version": "{{version}}" }
                  },
                  "compatibility": {
                    "minimumHostVersion": "0.1.0",
                    "maximumHostVersionExclusive": "1.0.0"
                  },
                  "requires": ["projects"],
                  "optionalDependencies": [],
                  "capabilities": ["api", "web"],
                  "artifacts": { "dotnetProject": "", "webPackage": "" },
                  "entrypoints": {
                    "dotnet": { "type": "Trykatch.Modules.Reporting.ReportingModule" },
                    "web": { "specifier": "@trykatchapp/module-reporting", "export": "reportingModule" }
                  },
                  "contributions": {
                    "permissions": ["reporting.read"],
                    "routes": [{ "id": "reporting.home", "path": "/reporting" }],
                    "extensionPoints": [],
                    "extensions": [],
                    "assistantTools": []
                  }
                }
                """);
            return path;
        }

        public string WriteSourceBundle(string id, string version)
        {
            string bundle = Path.Combine(Root, $"bundle-{id}-{version}");
            string dotnetDirectory = Path.Combine(bundle, "src/TrykatchApp.Modules.Reporting");
            string webDirectory = Path.Combine(bundle, "web/packages/module-reporting");
            Directory.CreateDirectory(dotnetDirectory);
            Directory.CreateDirectory(webDirectory);
            File.WriteAllText(Path.Combine(dotnetDirectory, "Reporting.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(dotnetDirectory, "ReportingModule.cs"), "namespace Trykatch.Modules.Reporting; public sealed class Marker;");
            File.WriteAllText(Path.Combine(webDirectory, "package.json"), "{ \"name\": \"@trykatchapp/module-reporting\" }");
            File.WriteAllText(Path.Combine(webDirectory, "index.ts"), "export const reportingModule = {}\n");
            File.WriteAllText(Path.Combine(bundle, "trykatch.module.json"), $$"""
                {
                  "schemaVersion": 1,
                  "id": "{{id}}",
                  "name": "Reporting",
                  "version": "{{version}}",
                  "description": "Ejected reporting module.",
                  "distribution": { "kind": "workspace", "license": "Apache-2.0" },
                  "compatibility": {
                    "minimumHostVersion": "0.1.0",
                    "maximumHostVersionExclusive": "1.0.0"
                  },
                  "requires": ["projects"],
                  "optionalDependencies": [],
                  "capabilities": ["api", "web"],
                  "artifacts": {
                    "dotnetProject": "src/TrykatchApp.Modules.Reporting/Reporting.csproj",
                    "webPackage": "web/packages/module-reporting/package.json"
                  },
                  "entrypoints": {
                    "dotnet": { "type": "Trykatch.Modules.Reporting.ReportingModule" },
                    "web": { "specifier": "@trykatchapp/module-reporting", "export": "reportingModule" }
                  },
                  "contributions": {
                    "permissions": ["reporting.read"],
                    "routes": [{ "id": "reporting.home", "path": "/reporting" }],
                    "extensionPoints": [],
                    "extensions": [],
                    "assistantTools": []
                  }
                }
                """);
            return bundle;
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
                  "distribution": {
                    "kind": "workspace",
                    "license": "Apache-2.0"
                  },
                  "compatibility": {
                    "minimumHostVersion": "0.1.0",
                    "maximumHostVersionExclusive": "1.0.0"
                  },
                  "requires": [{{string.Join(", ", requires.Select(value => $"\"{value}\""))}}],
                  "optionalDependencies": [],
                  "capabilities": ["api", "web"],
                  "artifacts": {
                    "dotnetProject": "src/{{(id == "projects" ? "Projects" : "SampleExtension")}}.csproj",
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

    private sealed class RecordingCommandRunner(bool failFirstCommand = false) : IWorkspaceCommandRunner
    {
        public List<string> Commands { get; } = [];

        public WorkspaceCommandResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
        {
            Commands.Add($"{fileName} {string.Join(' ', arguments)}");
            return failFirstCommand && Commands.Count == 1
                ? new(1, "simulated restore failure")
                : new(0, "ok");
        }
    }
}
