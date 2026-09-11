using Trykatch.ModuleTool;
using Trykatch.Modules;
using Shouldly;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class ModuleWorkspaceTests
{
    [TestMethod]
    [DataRow("subject")]
    [DataRow("builder")]
    [DataRow("stale")]
    [DataRow("vulnerable")]
    [DataRow("sbom")]
    [DataRow("signature")]
    [DataRow("signer")]
    [DataRow("frontend-version")]
    public void SignedButInvalidReleaseEvidenceCannotMutateTheWorkspace(string invalidEvidence)
    {
        using TemporaryModuleWorkspace temporary = TemporaryModuleWorkspace.Create();
        string candidate = temporary.WritePackageManifest("reporting", "1.0.0", invalidEvidence);
        ModuleWorkspace workspace = new(temporary.Root, new RecordingCommandRunner());
        workspace.Generate().IsHealthy.ShouldBeTrue();
        string[] guarded = ["try" + "katch.modules.json", "try" + "katch.modules.lock.json", "NuGet.Config", "Directory.Packages.props", "web/apps/web/package.json", "web/pnpm-lock.yaml"];
        Dictionary<string, byte[]> before = guarded.ToDictionary(path => path, path => File.ReadAllBytes(Path.Combine(temporary.Root, path)));

        Should.Throw<InvalidOperationException>(() => workspace.InstallPackage(candidate, Sha256(candidate)));

        foreach ((string path, byte[] bytes) in before) File.ReadAllBytes(Path.Combine(temporary.Root, path)).ShouldBe(bytes);
        Directory.Exists(Path.Combine(temporary.Root, ".trykatch/modules/reporting")).ShouldBeFalse();
    }

    [TestMethod]
    public void SignedInstallRestoresTheVerifiedBytesEvenIfOriginalArtifactsChangeAfterVerification()
    {
        using TemporaryModuleWorkspace temporary = TemporaryModuleWorkspace.Create();
        string candidate = temporary.WritePackageManifest("reporting", "1.0.0");
        string backend = Path.Combine(temporary.Root, "reporting.1.0.0.nupkg");
        string frontend = Path.Combine(temporary.Root, "reporting.1.0.0.tgz");
        string expectedBackend = Sha256(backend);
        string expectedFrontend = Sha256(frontend);
        string expectedFrontendIntegrity = "sha512-" + Convert.ToBase64String(
            SHA512.HashData(File.ReadAllBytes(frontend)));
        File.WriteAllText(Path.Combine(temporary.Root, "Trykatch.slnx"), """
            <Solution><Project Path="src/API/Trykatch.Api/Trykatch.Api.csproj"/><Project Path="src/API/Trykatch.Migrator/Trykatch.Migrator.csproj"/></Solution>
            """);
        File.WriteAllText(Path.Combine(temporary.Root, "Directory.Packages.props"), "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        foreach (string project in new[] { "Trykatch.Api", "Trykatch.Migrator" })
        {
            string directory = Path.Combine(temporary.Root, "src", "API", project);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, project + ".csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><NuGetAudit>false</NuGetAudit></PropertyGroup></Project>");
        }
        File.WriteAllText(Path.Combine(temporary.Root, "web/package.json"), "{\"name\":\"security-fixture\",\"private\":true}");
        File.WriteAllText(Path.Combine(temporary.Root, "web/pnpm-workspace.yaml"), "packages:\n  - apps/*\n");

        ModuleWorkspace workspace = new(temporary.Root, new ArtifactReplacementRunner(backend, frontend));
        workspace.Generate().IsHealthy.ShouldBeTrue();
        ModuleDoctorReport result = workspace.InstallPackage(candidate, Sha256(candidate));

        result.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, result.Errors));
        string pinnedBackend = Path.Combine(temporary.Root, ".trykatch/packages", expectedBackend, "Trykatch.Modules.Reporting.1.0.0.nupkg");
        Sha256(pinnedBackend).ShouldBe(expectedBackend);
        Sha256(Path.Combine(temporary.Root, ".trykatch/modules/reporting/1.0.0/reporting.1.0.0.tgz")).ShouldBe(expectedFrontend);
        File.ReadAllText(backend).ShouldBe("Changed after verification.");
        File.ReadAllText(Path.Combine(temporary.Root, "web/apps/web/package.json"))
            .ShouldContain("../../../.trykatch/modules/reporting/1.0.0/reporting.1.0.0.tgz");
        File.ReadAllText(Path.Combine(temporary.Root, "web/pnpm-lock.yaml")).ShouldContain(expectedFrontendIntegrity);
    }

    [TestMethod]
    public void InstallRejectsRootedArtifactPathsBeforeChangingWorkspaceFiles()
    {
        using TemporaryModuleWorkspace temporary = TemporaryModuleWorkspace.Create();
        string candidate = temporary.WritePackageManifest("reporting", "1.0.0");
        ModuleWorkspace workspace = new(temporary.Root, new RecordingCommandRunner());
        workspace.Generate().IsHealthy.ShouldBeTrue();
        string catalogPath = Path.Combine(temporary.Root, "try" + "katch.modules.json");
        byte[] before = File.ReadAllBytes(catalogPath);
        System.Text.Json.Nodes.JsonNode manifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(candidate))!;
        manifest["distribution"]!["web"]!["packageFile"] = Path.Combine(temporary.Root, "reporting.1.0.0.tgz");
        File.WriteAllText(candidate, manifest.ToJsonString());

        Should.Throw<InvalidOperationException>(() => workspace.InstallPackage(candidate, Sha256(candidate)))
            .Message.ShouldContain("relative path");

        File.ReadAllBytes(catalogPath).ShouldBe(before);
        Directory.Exists(Path.Combine(temporary.Root, ".trykatch/modules/reporting")).ShouldBeFalse();
    }

    [TestMethod]
    public void InstallRejectsAnUnrelatedArchiveBeforeChangingWorkspaceFiles()
    {
        using TemporaryModuleWorkspace temporary = TemporaryModuleWorkspace.Create();
        new ModuleWorkspace(temporary.Root).Generate().IsHealthy.ShouldBeTrue();
        string candidate = temporary.WritePackageManifest("reporting", "1.0.0");
        string original = File.ReadAllText(Path.Combine(temporary.Root, "try" + "katch.modules.json"));
        System.Text.Json.Nodes.JsonNode manifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(candidate))!;
        string package = Path.Combine(temporary.Root, manifest["distribution"]!["dotnet"]!["packageFile"]!.GetValue<string>());
        File.Delete(package);
        using (System.IO.Compression.ZipArchive archive = System.IO.Compression.ZipFile.Open(package, System.IO.Compression.ZipArchiveMode.Create))
        {
            using StreamWriter writer = new(archive.CreateEntry("unrelated.nuspec").Open());
            writer.Write("<package><metadata><id>Unrelated.Package</id><version>9.9.9</version></metadata></package>");
        }
        manifest["distribution"]!["dotnet"]!["sha256"] = Sha256(package);
        File.WriteAllText(candidate, manifest.ToJsonString());

        Should.Throw<InvalidOperationException>(() => new ModuleWorkspace(temporary.Root, new RecordingCommandRunner())
            .InstallPackage(candidate, Sha256(candidate)));
        File.ReadAllText(Path.Combine(temporary.Root, "try" + "katch.modules.json")).ShouldBe(original);
    }

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
        File.ReadAllText(Path.Combine(temporary.Root, "try" + "katch.modules.lock.json")).ShouldContain("manifestSha256");
    }

    [TestMethod]
    public void CliAccessRulesExhaustivelyMatchTheRuntimeContract()
    {
        Dictionary<string, ModuleDataOwnership> ownerships = new(StringComparer.Ordinal)
        {
            ["organization"] = ModuleDataOwnership.Organization,
            ["platform"] = ModuleDataOwnership.Platform,
            ["global"] = ModuleDataOwnership.Global,
            ["infrastructure"] = ModuleDataOwnership.Infrastructure
        };
        Dictionary<string, ModuleDataAccessRule> accessRules = new(StringComparer.Ordinal)
        {
            ["platform-only"] = ModuleDataAccessRule.PlatformOnly,
            ["identity-only"] = ModuleDataAccessRule.IdentityOnly,
            ["global-read-only"] = ModuleDataAccessRule.GlobalReadOnly,
            ["host-only"] = ModuleDataAccessRule.HostOnly,
            ["outbox-append-only"] = ModuleDataAccessRule.OutboxAppendOnly
        };
        string[] schemas = ["app", "platform", "identity", "reference", "infrastructure", "custom"];
        string[] ownershipValues = [.. ownerships.Keys, "unknown"];
        string?[] accessValues = [null, .. accessRules.Keys, "unknown"];

        foreach (string ownership in ownershipValues)
        foreach (string schema in schemas)
        foreach (string? accessRule in accessValues)
        {
            bool runtimeApproved = false;
            if (ownerships.TryGetValue(ownership, out ModuleDataOwnership runtimeOwnership)
                && (accessRule is null || accessRules.TryGetValue(accessRule, out _)))
            {
                ModuleDataAccessRule? runtimeAccess = accessRule is null ? null : accessRules[accessRule];
                DataResourceDescriptor resource = new(
                    "parity", schema, "records", runtimeOwnership,
                    IsolationPolicy: runtimeOwnership == ModuleDataOwnership.Organization ? "records_organization_isolation" : null,
                    AccessRule: runtimeAccess);
                try
                {
                    ModuleDataResourceRules.Validate(resource);
                    runtimeApproved = true;
                }
                catch (InvalidOperationException)
                {
                    runtimeApproved = false;
                }
            }

            ModuleWorkspace.IsApprovedDataResourceAccess(ownership, schema, accessRule)
                .ShouldBe(runtimeApproved, $"CLI/runtime access-rule mismatch for {ownership}/{schema}/{accessRule ?? "<null>"}");
        }
    }

    [TestMethod]
    public void WorkspaceManifestDigestSurvivesDotnetTemplateNameReplacement()
    {
        using TemporaryModuleWorkspace temporary = TemporaryModuleWorkspace.Create();
        ModuleWorkspace workspace = new(temporary.Root);
        workspace.Generate().IsHealthy.ShouldBeTrue();
        string lockBeforeRename = File.ReadAllText(Path.Combine(temporary.Root, "try" + "katch.modules.lock.json"));

        string catalogPath = Path.Combine(temporary.Root, "try" + "katch.modules.json");
        string manifestPath = Path.Combine(temporary.Root, "manifests/projects.json");
        File.WriteAllText(
            catalogPath,
            File.ReadAllText(catalogPath)
                .Replace("Trykatch", "Horizon", StringComparison.Ordinal)
                .Replace("@trykatch", "@horizon", StringComparison.Ordinal));
        File.WriteAllText(
            manifestPath,
            File.ReadAllText(manifestPath)
                .Replace("Trykatch", "Horizon", StringComparison.Ordinal)
                .Replace("@trykatch", "@horizon", StringComparison.Ordinal));

        ModuleDoctorReport renamed = new ModuleWorkspace(temporary.Root).Generate();

        renamed.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, renamed.Errors));
        File.ReadAllText(Path.Combine(temporary.Root, "src/GeneratedModules.cs"))
            .ShouldContain("using Horizon.Modules;");
        File.ReadAllText(Path.Combine(temporary.Root, "web/src/modules.ts"))
            .ShouldContain("from '@horizon/module-sdk'");
        File.ReadAllText(Path.Combine(temporary.Root, "try" + "katch.modules.lock.json")).ShouldBe(lockBeforeRename);
        lockBeforeRename.ShouldContain("template-normalized-sha256");
    }

    [TestMethod]
    public void LegacyVersionOneCatalogDerivesPortableOutputIdentities()
    {
        using TemporaryModuleWorkspace temporary = TemporaryModuleWorkspace.Create();
        string catalogPath = Path.Combine(temporary.Root, "try" + "katch.modules.json");
        string catalog = File.ReadAllText(catalogPath)
            .Replace("    \"dotnetModuleContractNamespace\": \"Trykatch.Modules\",\n", string.Empty, StringComparison.Ordinal)
            .Replace("    \"web\": \"web/src/modules.ts\",\n", "    \"web\": \"web/src/modules.ts\"\n", StringComparison.Ordinal)
            .Replace("    \"webModuleSdkSpecifier\": \"@trykatch/module-sdk\"\n", string.Empty, StringComparison.Ordinal)
            .Replace("Trykatch", "CustomerPortal", StringComparison.Ordinal)
            .Replace("@trykatch", "@customerportal", StringComparison.Ordinal);
        File.WriteAllText(catalogPath, catalog);
        string manifestPath = Path.Combine(temporary.Root, "manifests/projects.json");
        File.WriteAllText(
            manifestPath,
            File.ReadAllText(manifestPath)
                .Replace("Trykatch", "CustomerPortal", StringComparison.Ordinal)
                .Replace("@trykatch", "@customerportal", StringComparison.Ordinal));

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
        string before = File.ReadAllText(Path.Combine(temporary.Root, "try" + "katch.modules.json"));

        ModuleDoctorReport report = workspace.SetEnabled("projects", enabled: false);

        report.IsHealthy.ShouldBeFalse();
        report.Errors.ShouldContain(error => error.Contains("requires disabled module 'projects'", StringComparison.Ordinal));
        File.ReadAllText(Path.Combine(temporary.Root, "try" + "katch.modules.json")).ShouldBe(before);
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
        string catalogPath = Path.Combine(temporary.Root, "try" + "katch.modules.json");
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
            .ShouldContain("@trykatch/module-reporting");
        File.ReadAllText(Path.Combine(temporary.Root, "try" + "katch.modules.lock.json"))
            .ShouldContain(digest);
        runner.Commands.Count.ShouldBe(3);

        ModuleDoctorReport removed = workspace.Unregister("reporting");

        removed.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, removed.Errors));
        removed.Modules.ShouldNotContain(module => module.Id == "reporting");
        File.ReadAllText(Path.Combine(temporary.Root, "Directory.Packages.props"))
            .ShouldNotContain("Trykatch.Modules.Reporting");
        File.Exists(Path.Combine(temporary.Root, "manifests/projects.json")).ShouldBeTrue();
        runner.Commands.Count.ShouldBe(5);
    }

    [TestMethod]
    public void InstallRollsBackEveryWorkspaceFileWhenPackageRestoreFails()
    {
        using TemporaryModuleWorkspace temporary = TemporaryModuleWorkspace.Create();
        ModuleWorkspace baseline = new(temporary.Root);
        baseline.Generate().IsHealthy.ShouldBeTrue();
        string candidatePath = temporary.WritePackageManifest("reporting", "1.0.0");
        string catalogBefore = File.ReadAllText(Path.Combine(temporary.Root, "try" + "katch.modules.json"));
        string packagesBefore = File.ReadAllText(Path.Combine(temporary.Root, "Directory.Packages.props"));
        string configBefore = File.ReadAllText(Path.Combine(temporary.Root, "NuGet.Config"));
        RecordingCommandRunner runner = new(failRestore: true);

        Should.Throw<InvalidOperationException>(() =>
            new ModuleWorkspace(temporary.Root, runner).InstallPackage(candidatePath, Sha256(candidatePath)));

        File.ReadAllText(Path.Combine(temporary.Root, "try" + "katch.modules.json")).ShouldBe(catalogBefore);
        File.ReadAllText(Path.Combine(temporary.Root, "Directory.Packages.props")).ShouldBe(packagesBefore);
        File.ReadAllText(Path.Combine(temporary.Root, "NuGet.Config")).ShouldBe(configBefore);
        Directory.EnumerateFiles(Path.Combine(temporary.Root, ".trykatch/modules/reporting"), "*", SearchOption.AllDirectories).ShouldBeEmpty();
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
        File.ReadAllText(Path.Combine(temporary.Root, "try" + "katch.modules.lock.json"))
            .ShouldContain("\"version\": \"1.1.0\"");
    }

    [TestMethod]
    public async Task ConcurrentDisableCannotBeOverwrittenByAnInFlightUpgrade()
    {
        using TemporaryModuleWorkspace temporary = TemporaryModuleWorkspace.Create();
        RecordingCommandRunner upgradeRunner = new();
        ModuleWorkspace workspace = new(temporary.Root, upgradeRunner);
        workspace.Generate().IsHealthy.ShouldBeTrue();
        string versionOne = temporary.WritePackageManifest("reporting", "1.0.0");
        workspace.InstallPackage(versionOne, Sha256(versionOne)).IsHealthy.ShouldBeTrue();
        workspace.SetEnabled("reporting", enabled: true).IsHealthy.ShouldBeTrue();
        string versionTwo = temporary.WritePackageManifest("reporting", "1.1.0");
        using ManualResetEventSlim restoreStarted = new();
        using ManualResetEventSlim continueRestore = new();
        upgradeRunner.BlockNextPnpmInstall(restoreStarted, continueRestore);

        Task<ModuleDoctorReport> upgrade = Task.Run(() =>
            workspace.UpgradePackage(versionTwo, Sha256(versionTwo)));
        restoreStarted.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue("upgrade did not reach its serialized package restore");
        Task<ModuleDoctorReport> disable = Task.Run(() =>
            new ModuleWorkspace(temporary.Root, new RecordingCommandRunner()).SetEnabled("reporting", enabled: false));
        await Task.Delay(100);
        disable.IsCompleted.ShouldBeFalse("disable must wait for the active workspace mutation");

        continueRestore.Set();
        ModuleDoctorReport[] results = await Task.WhenAll(upgrade, disable);

        results.ShouldAllBe(result => result.IsHealthy);
        ModuleStatus final = new ModuleWorkspace(temporary.Root).Inspect().Modules.Single(module => module.Id == "reporting");
        final.Version.ShouldBe("1.1.0");
        final.Enabled.ShouldBeFalse("the later disable must win after rereading the upgraded catalog");
    }

    [TestMethod]
    public void EquivalentWorkspacePathSpellingsShareOneMutationLockIdentity()
    {
        using TemporaryModuleWorkspace temporary = TemporaryModuleWorkspace.Create();

        string canonical = ModuleWorkspace.PackageMutationLockName(temporary.Root);
        string trailingSeparator = ModuleWorkspace.PackageMutationLockName(temporary.Root + Path.DirectorySeparatorChar);
        string dotSegment = ModuleWorkspace.PackageMutationLockName(Path.Combine(temporary.Root, "."));

        trailingSeparator.ShouldBe(canonical);
        dotSegment.ShouldBe(canonical);
    }

    [TestMethod]
    public void WorkspacePathsThroughASymlinkedAncestorShareOneMutationLockIdentity()
    {
        if (OperatingSystem.IsWindows())
            return; // Creating directory symlinks is not reliably available to unprivileged Windows test processes.

        using TemporaryModuleWorkspace temporary = TemporaryModuleWorkspace.Create();
        string parent = Path.GetDirectoryName(temporary.Root)!;
        string aliasParent = Path.Combine(parent, $"trykatch-module-alias-{Guid.NewGuid():N}");
        Directory.CreateSymbolicLink(aliasParent, parent);
        try
        {
            string aliasRoot = Path.Combine(aliasParent, Path.GetFileName(temporary.Root));
            Directory.Exists(aliasRoot).ShouldBeTrue();

            ModuleWorkspace.PackageMutationLockName(aliasRoot)
                .ShouldBe(ModuleWorkspace.PackageMutationLockName(temporary.Root));
        }
        finally
        {
            Directory.Delete(aliasParent);
        }
    }

    [TestMethod]
    public void RegisterWorkspaceAcceptsAnAbsoluteManifestPathThroughTheRootAlias()
    {
        if (OperatingSystem.IsWindows())
            return; // Creating directory symlinks is not reliably available to unprivileged Windows test processes.

        using TemporaryModuleWorkspace temporary = TemporaryModuleWorkspace.Create();
        string bundle = temporary.WriteSourceBundle("reporting", "1.0.0");
        string dotnetTarget = Path.Combine(temporary.Root, "src/Trykatch.Modules.Reporting");
        string webTarget = Path.Combine(temporary.Root, "web/packages/module-reporting");
        Directory.CreateDirectory(dotnetTarget);
        Directory.CreateDirectory(webTarget);
        File.Copy(Path.Combine(bundle, "src/Trykatch.Modules.Reporting/Reporting.csproj"), Path.Combine(dotnetTarget, "Reporting.csproj"));
        File.Copy(Path.Combine(bundle, "web/packages/module-reporting/package.json"), Path.Combine(webTarget, "package.json"));

        string parent = Path.GetDirectoryName(temporary.Root)!;
        string aliasParent = Path.Combine(parent, $"trykatch-module-alias-{Guid.NewGuid():N}");
        Directory.CreateSymbolicLink(aliasParent, parent);
        try
        {
            string aliasRoot = Path.Combine(aliasParent, Path.GetFileName(temporary.Root));
            string aliasManifest = Path.Combine(aliasRoot, Path.GetRelativePath(temporary.Root, bundle), "try" + "katch.module.json");

            ModuleDoctorReport result = new ModuleWorkspace(aliasRoot, new RecordingCommandRunner())
                .RegisterWorkspace(aliasManifest);

            result.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, result.Errors));
            result.Modules.Single(module => module.Id == "reporting").Enabled.ShouldBeFalse();
            File.ReadAllText(Path.Combine(temporary.Root, "try" + "katch.modules.json"))
                .ShouldContain("bundle-reporting-1.0.0/" + "try" + "katch.module.json");
        }
        finally
        {
            Directory.Delete(aliasParent);
        }
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
        string bundleManifest = Path.Combine(bundle, "try" + "katch.module.json");

        ModuleDoctorReport ejected = workspace.EjectPackage("reporting", bundle, Sha256(bundleManifest));

        ejected.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, ejected.Errors));
        string dotnetProject = Path.Combine(temporary.Root, "src/Trykatch.Modules.Reporting/Reporting.csproj");
        File.Exists(dotnetProject).ShouldBeTrue();
        File.ReadAllText(Path.Combine(temporary.Root, "src/API/Trykatch.Api/Trykatch.Api.csproj"))
            .ShouldContain("ProjectReference");
        File.ReadAllText(Path.Combine(temporary.Root, "web/apps/web/package.json"))
            .ShouldContain("workspace:*");
        File.ReadAllText(Path.Combine(temporary.Root, "try" + "katch.modules.lock.json"))
            .ShouldContain("\"kind\": \"workspace\"");
    }

    [TestMethod]
    public void BuiltCompositePackagesCompleteThePackageLifecycle()
    {
        string? packageRoot = Environment.GetEnvironmentVariable("TRYKATCH_ACTUAL_MODULE_PACKAGES");
        string? moduleSourceRoot = Environment.GetEnvironmentVariable("TRYKATCH_ACTUAL_MODULE_SOURCE_ROOT");
        if (string.IsNullOrWhiteSpace(packageRoot) || string.IsNullOrWhiteSpace(moduleSourceRoot))
            return; // The package CI supplies two freshly packed versions of every discovered module.

        string versionOneRoot = Path.Combine(packageRoot, "1.0.0");
        string versionTwoRoot = Path.Combine(packageRoot, "1.1.0");
        string[] versionOnePackages = Directory.GetFiles(versionOneRoot, "Trykatch.Modules.*.1.0.0.nupkg")
            .Order(StringComparer.Ordinal)
            .ToArray();
        versionOnePackages.Length.ShouldBeGreaterThan(0);

        foreach (string versionOnePackage in versionOnePackages)
        {
            string packageId = Path.GetFileName(versionOnePackage)[..^".1.0.0.nupkg".Length];
            string moduleName = packageId["Trykatch.Modules.".Length..];
            string moduleId = moduleName.ToLowerInvariant();
            string versionTwoPackage = Path.Combine(versionTwoRoot, $"{packageId}.1.1.0.nupkg");
            File.Exists(versionTwoPackage).ShouldBeTrue($"upgrade package missing for {moduleName}");

            using TemporaryModuleWorkspace temporary = TemporaryModuleWorkspace.Create();
            if (moduleId == "projects")
                temporary.RenameBaselineProjectsModule();
            ActualPackageLifecycleCommandRunner runner = new();
            ModuleWorkspace workspace = new(temporary.Root, runner);
            workspace.Generate().IsHealthy.ShouldBeTrue();
            string versionOneManifest = temporary.WriteActualPackageManifest(
                moduleId, moduleName, "1.0.0", versionOnePackage, moduleSourceRoot);

            ModuleDoctorReport installed = workspace.InstallPackage(versionOneManifest, Sha256(versionOneManifest));

            installed.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, installed.Errors));
            installed.Modules.Single(module => module.Id == moduleId).Version.ShouldBe("1.0.0");

            string versionTwoManifest = temporary.WriteActualPackageManifest(
                moduleId, moduleName, "1.1.0", versionTwoPackage, moduleSourceRoot);
            ModuleDoctorReport upgraded = workspace.UpgradePackage(versionTwoManifest, Sha256(versionTwoManifest));

            upgraded.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, upgraded.Errors));
            upgraded.Modules.Single(module => module.Id == moduleId).Version.ShouldBe("1.1.0");

            string sourceBundle = temporary.WriteCompositeSourceBundle(moduleId, moduleName, "1.1.0", moduleSourceRoot);
            string sourceManifest = Path.Combine(sourceBundle, "try" + "katch.module.json");
            string guardedSource = Directory.GetFiles(
                Path.Combine(sourceBundle, "src", "Modules", moduleName),
                "*.csproj",
                SearchOption.AllDirectories)[0];
            byte[] reviewedSource = File.ReadAllBytes(guardedSource);
            File.AppendAllText(guardedSource, "<!-- tampered -->");
            Should.Throw<InvalidOperationException>(() =>
                workspace.EjectPackage(moduleId, sourceBundle, Sha256(sourceManifest)))
                .Message.ShouldContain("tree integrity");
            File.WriteAllBytes(guardedSource, reviewedSource);
            ModuleDoctorReport ejected = workspace.EjectPackage(moduleId, sourceBundle, Sha256(sourceManifest));

            ejected.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, ejected.Errors));
            ejected.Modules.Single(module => module.Id == moduleId).ManifestPath.ShouldContain("src/Modules");
            string ejectedRoot = Path.Combine(temporary.Root, "src", "Modules", moduleName);
            Directory.GetFiles(ejectedRoot, "*.csproj", SearchOption.AllDirectories).Length.ShouldBe(5);
            File.ReadAllText(Path.Combine(ejectedRoot, "trykatch.module.json"))
                .ShouldContain(moduleId == "documents" ? "\"requires\": [\n    \"projects\"" : "\"requires\": []");

            using TemporaryModuleWorkspace unregisterTemporary = TemporaryModuleWorkspace.Create();
            if (moduleId == "projects")
                unregisterTemporary.RenameBaselineProjectsModule();
            ModuleWorkspace unregisterWorkspace = new(unregisterTemporary.Root, new ActualPackageLifecycleCommandRunner());
            unregisterWorkspace.Generate().IsHealthy.ShouldBeTrue();
            string unregisterManifest = unregisterTemporary.WriteActualPackageManifest(
                moduleId, moduleName, "1.0.0", versionOnePackage, moduleSourceRoot);
            unregisterWorkspace.InstallPackage(unregisterManifest, Sha256(unregisterManifest)).IsHealthy.ShouldBeTrue();

            ModuleDoctorReport unregistered = unregisterWorkspace.Unregister(moduleId);

            unregistered.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, unregistered.Errors));
            unregistered.Modules.ShouldNotContain(module => module.Id == moduleId);
        }
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
            Directory.CreateDirectory(Path.Combine(root, "src/API/Trykatch.Api"));
            Directory.CreateDirectory(Path.Combine(root, "src/API/Trykatch.Migrator"));
            Directory.CreateDirectory(Path.Combine(root, "web/apps/web"));
            File.WriteAllText(Path.Combine(root, "Trykatch.slnx"), "<Solution />");
            File.WriteAllText(Path.Combine(root, "src/Projects.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(root, "src/SampleExtension.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(root, "web/packages/projects/package.json"), "{}");
            File.WriteAllText(Path.Combine(root, "web/packages/sample-extension/package.json"), "{}");
            File.WriteAllText(Path.Combine(root, "Directory.Packages.props"),
                "<Project><ItemGroup><PackageVersion Include=\"Existing.Package\" Version=\"1.0.0\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(root, "src/API/Trykatch.Api/Trykatch.Api.csproj"),
                "<Project><ItemGroup><PackageReference Include=\"Existing.Package\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(root, "src/API/Trykatch.Migrator/Trykatch.Migrator.csproj"),
                "<Project><ItemGroup><PackageReference Include=\"Existing.Package\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(root, "web/apps/web/package.json"),
                "{\n  \"dependencies\": {}\n}\n");
            File.WriteAllText(Path.Combine(root, "web/package.json"),
                "{ \"name\": \"trykatch-module-lifecycle\", \"private\": true }\n");
            File.WriteAllText(Path.Combine(root, "web/pnpm-workspace.yaml"), """
                packages:
                  - apps/*
                  - packages/*
                  - ../src/Modules/*/Web
                """);
            WriteWorkspacePackage(root, "api-client", "@trykatch/api-client");
            WriteWorkspacePackage(root, "module-sdk", "@trykatch/module-sdk");
            WriteWorkspacePackage(root, "ui", "@trykatch/ui");
            File.WriteAllText(Path.Combine(root, "web/pnpm-lock.yaml"), "lockfileVersion: '9.0'\n");
            File.WriteAllText(Path.Combine(root, "NuGet.Config"), """
                <configuration>
                  <config><add key="signatureValidationMode" value="require" /></config>
                  <trustedSigners><author name="trykatch"><certificate fingerprint="00" hashAlgorithm="SHA256" allowUntrustedRoot="false" /></author></trustedSigners>
                  <packageSourceMapping><packageSource key="nuget.org"><package pattern="*" /></packageSource></packageSourceMapping>
                </configuration>
                """);

            File.WriteAllText(Path.Combine(root, "manifests/projects.json"), Manifest(
                "projects",
                "Trykatch.Modules.Projects.Infrastructure.ProjectsModule",
                "./projects",
                "projectsModule",
                [],
                "/projects"));
            if (includeDependent)
            {
                File.WriteAllText(Path.Combine(root, "manifests/sample-extension.json"), Manifest(
                    "sample-extension",
                    "Trykatch.Modules.SampleExtension.SampleExtensionModule",
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
            File.WriteAllText(Path.Combine(root, "try" + "katch.modules.json"), $$"""
                {
                  "schemaVersion": 1,
                  "hostVersion": "0.1.0",
                  "lockFile": "try\u006batch.modules.lock.json",
                  "trustedPublishers": ["trykatch"],
                  "outputs": {
                    "backend": "src/GeneratedModules.cs",
                    "backendNamespace": "Trykatch.Api.Modules",
                    "dotnetModuleContractNamespace": "Trykatch.Modules",
                    "migrator": "src/GeneratedMigratorModules.cs",
                    "migratorNamespace": "Trykatch.Migrator.Modules",
                    "web": "web/src/modules.ts",
                    "webModuleSdkSpecifier": "@trykatch/module-sdk"
                  },
                  "modules": [
                    {{registrations}}
                  ]
                }
                """);
            return new(root);
        }

        private static void WriteWorkspacePackage(string root, string directory, string packageName)
        {
            string path = Path.Combine(root, "web", "packages", directory);
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "package.json"),
                $"{{ \"name\": \"{packageName}\", \"version\": \"1.0.0\" }}\n");
        }

        public string WritePackageManifest(string id, string version, string? invalidEvidence = null)
        {
            string path = Path.Combine(Root, $"{id}-{version}.package.json");
            string packageFile = $"{id}.{version}.nupkg";
            string provenanceFile = $"{id}.{version}.provenance.json";
            string sbomFile = $"{id}.{version}.spdx.json";
            SignedModuleTestArtifacts.Create(Root, id, version, invalidEvidence);
            File.WriteAllText(path, $$"""
                {
                  "schemaVersion": 1,
                  "id": "{{id}}",
                  "name": "Reporting",
                  "version": "{{version}}",
                  "description": "Packaged reporting module.",
                  "publisher": "trykatch",
                  "distribution": {
                    "kind": "package",
                    "license": "Apache-2.0",
                    "dotnet": {
                      "id": "Trykatch.Modules.Reporting",
                      "version": "{{version}}",
                      "packageFile": "{{packageFile}}",
                      "sha256": "{{Sha256(Path.Combine(Root, packageFile))}}"
                    },
                    "web": { "id": "@trykatch/module-reporting", "version": "{{version}}", "packageFile": "{{id}}.{{version}}.tgz", "sha256": "{{Sha256(Path.Combine(Root, $"{id}.{version}.tgz"))}}" },
                    "supplyChain": {
                      "provenanceFile": "{{provenanceFile}}",
                      "provenanceSha256": "{{Sha256(Path.Combine(Root, provenanceFile))}}",
                      "provenanceSignatureFile": "{{id}}.{{version}}.provenance.sig",
                      "sbomFile": "{{sbomFile}}",
                      "sbomSha256": "{{Sha256(Path.Combine(Root, sbomFile))}}"
                    }
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
                    "web": { "specifier": "@trykatch/module-reporting", "export": "reportingModule" }
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

        public void RenameBaselineProjectsModule()
        {
            string projectsManifest = Path.Combine(Root, "manifests/projects.json");
            string foundationManifest = Path.Combine(Root, "manifests/foundation.json");
            string manifest = File.ReadAllText(projectsManifest)
                .Replace("\"id\": \"projects\"", "\"id\": \"foundation\"", StringComparison.Ordinal)
                .Replace("\"name\": \"projects\"", "\"name\": \"foundation\"", StringComparison.Ordinal)
                .Replace("\"path\": \"/projects\"", "\"path\": \"/foundation\"", StringComparison.Ordinal);
            File.WriteAllText(foundationManifest, manifest);
            File.Delete(projectsManifest);

            string catalogPath = Path.Combine(Root, "try" + "katch.modules.json");
            string catalog = File.ReadAllText(catalogPath)
                .Replace("\"id\": \"projects\"", "\"id\": \"foundation\"", StringComparison.Ordinal)
                .Replace("manifests/projects.json", "manifests/foundation.json", StringComparison.Ordinal);
            File.WriteAllText(catalogPath, catalog);
        }

        public string WriteActualPackageManifest(
            string id,
            string moduleName,
            string version,
            string backendPackageSource,
            string moduleSourceRoot)
        {
            string path = Path.Combine(Root, $"{id}-{version}.package.json");
            string packageFile = $"{id}.{version}.nupkg";
            string provenanceFile = $"{id}.{version}.provenance.json";
            string sbomFile = $"{id}.{version}.spdx.json";
            string dotnetPackageId = $"Trykatch.Modules.{moduleName}";
            string frontendPackageId = $"@trykatch-modules/{id}";
            SignedModuleTestArtifacts.Create(
                Root,
                id,
                version,
                dotnetPackageId: dotnetPackageId,
                frontendPackageId: frontendPackageId,
                backendPackageSource: backendPackageSource);
            JsonObject manifest = ReadActualManifest(moduleSourceRoot, moduleName);
            manifest["version"] = version;
            manifest["description"] = "Actual composite package lifecycle verification.";
            manifest["distribution"] = new JsonObject
            {
                ["kind"] = "package",
                ["license"] = "Apache-2.0",
                ["dotnet"] = new JsonObject
                {
                    ["id"] = dotnetPackageId,
                    ["version"] = version,
                    ["packageFile"] = packageFile,
                    ["sha256"] = Sha256(Path.Combine(Root, packageFile))
                },
                ["web"] = new JsonObject
                {
                    ["id"] = frontendPackageId,
                    ["version"] = version,
                    ["packageFile"] = $"{id}.{version}.tgz",
                    ["sha256"] = Sha256(Path.Combine(Root, $"{id}.{version}.tgz"))
                },
                ["supplyChain"] = new JsonObject
                {
                    ["provenanceFile"] = provenanceFile,
                    ["provenanceSha256"] = Sha256(Path.Combine(Root, provenanceFile)),
                    ["provenanceSignatureFile"] = $"{id}.{version}.provenance.sig",
                    ["sbomFile"] = sbomFile,
                    ["sbomSha256"] = Sha256(Path.Combine(Root, sbomFile))
                }
            };
            manifest["artifacts"] = new JsonObject { ["dotnetProject"] = "", ["webPackage"] = "" };
            manifest["entrypoints"]!["web"]!["specifier"] = frontendPackageId;
            File.WriteAllText(path, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            return path;
        }

        public string WriteCompositeSourceBundle(
            string id,
            string moduleName,
            string version,
            string moduleSourceRoot)
        {
            string bundle = Path.Combine(Root, $"bundle-{id}-{version}");
            string sourceRoot = $"src/Modules/{moduleName}";
            string source = Path.Combine(moduleSourceRoot, moduleName);
            string target = Path.Combine(bundle, sourceRoot);
            CopyDirectory(source, target);
            JsonObject manifest = ReadActualManifest(moduleSourceRoot, moduleName);
            manifest["version"] = version;
            manifest["description"] = "Reviewed actual five-project ejection source.";
            manifest["distribution"] = new JsonObject { ["kind"] = "workspace", ["license"] = "Apache-2.0" };
            JsonObject artifacts = manifest["artifacts"]!.AsObject();
            artifacts["dotnetProject"] = artifacts["dotnetProject"]!.GetValue<string>()
                .Replace($"src/Modules/{moduleName}", sourceRoot, StringComparison.Ordinal);
            artifacts["webPackage"] = artifacts["webPackage"]!.GetValue<string>()
                .Replace($"src/Modules/{moduleName}", sourceRoot, StringComparison.Ordinal);
            artifacts["sourceRoot"] = sourceRoot;
            artifacts["sourceTreeSha256"] = SourceTreeSha256(target);
            File.WriteAllText(
                Path.Combine(bundle, "try" + "katch.module.json"),
                manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            return bundle;
        }

        private static JsonObject ReadActualManifest(string moduleSourceRoot, string moduleName) =>
            JsonNode.Parse(File.ReadAllText(Path.Combine(moduleSourceRoot, moduleName, "try" + "katch.module.json")))!
                .AsObject();

        private static void CopyDirectory(string source, string destination)
        {
            static bool IsBuildOutput(string path) => path
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj");

            foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)
                         .Where(directory => !IsBuildOutput(Path.GetRelativePath(source, directory))))
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                         .Where(file => !IsBuildOutput(Path.GetRelativePath(source, file)))
                         .Where(file => !string.Equals(
                             Path.GetFileName(file),
                             "try" + "katch.module.json",
                             StringComparison.Ordinal)))
            {
                string target = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }

        private static string SourceTreeSha256(string sourceRoot)
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
                string digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
                inventory.Append(relative).Append('\0').Append(digest).Append('\n');
            }
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(inventory.ToString())))
                .ToLowerInvariant();
        }

        public string WriteSourceBundle(string id, string version)
        {
            string bundle = Path.Combine(Root, $"bundle-{id}-{version}");
            string dotnetDirectory = Path.Combine(bundle, "src/Trykatch.Modules.Reporting");
            string webDirectory = Path.Combine(bundle, "web/packages/module-reporting");
            Directory.CreateDirectory(dotnetDirectory);
            Directory.CreateDirectory(webDirectory);
            File.WriteAllText(Path.Combine(dotnetDirectory, "Reporting.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(dotnetDirectory, "ReportingModule.cs"), "namespace Trykatch.Modules.Reporting; public sealed class Marker;");
            File.WriteAllText(Path.Combine(webDirectory, "package.json"), "{ \"name\": \"@trykatch/module-reporting\" }");
            File.WriteAllText(Path.Combine(webDirectory, "index.ts"), "export const reportingModule = {}\n");
            File.WriteAllText(Path.Combine(bundle, "try" + "katch.module.json"), $$"""
                {
                  "schemaVersion": 1,
                  "id": "{{id}}",
                  "name": "Reporting",
                  "version": "{{version}}",
                  "description": "Ejected reporting module.",
                  "publisher": "trykatch",
                  "distribution": { "kind": "workspace", "license": "Apache-2.0" },
                  "compatibility": {
                    "minimumHostVersion": "0.1.0",
                    "maximumHostVersionExclusive": "1.0.0"
                  },
                  "requires": ["projects"],
                  "optionalDependencies": [],
                  "capabilities": ["api", "web"],
                  "artifacts": {
                    "dotnetProject": "src/Trykatch.Modules.Reporting/Reporting.csproj",
                    "webPackage": "web/packages/module-reporting/package.json"
                  },
                  "entrypoints": {
                    "dotnet": { "type": "Trykatch.Modules.Reporting.ReportingModule" },
                    "web": { "specifier": "@trykatch/module-reporting", "export": "reportingModule" }
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
                  "publisher": "trykatch",
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

    private sealed class RecordingCommandRunner(bool failRestore = false) : IWorkspaceCommandRunner
    {
        private ManualResetEventSlim? restoreStarted;
        private ManualResetEventSlim? continueRestore;
        public List<string> Commands { get; } = [];

        public void BlockNextPnpmInstall(ManualResetEventSlim started, ManualResetEventSlim continuation)
        {
            restoreStarted = started;
            continueRestore = continuation;
        }

        public WorkspaceCommandResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
        {
            Commands.Add($"{fileName} {string.Join(' ', arguments)}");
            if (fileName == "dotnet" && arguments.Count > 1 && arguments[0] == "nuget" && arguments[1] == "verify")
                return new ProcessWorkspaceCommandRunner().Run(fileName, arguments, workingDirectory);
            if (fileName == "dotnet" && arguments[0] == "restore")
            {
                if (failRestore) return new(1, "simulated restore failure");
                System.Xml.Linq.XDocument config = System.Xml.Linq.XDocument.Load(Path.Combine(workingDirectory, "NuGet.Config"));
                string? cache = config.Descendants("add").SingleOrDefault(item => (string?)item.Attribute("key") == "globalPackagesFolder")?.Attribute("value")?.Value;
                if (cache is not null)
                {
                    foreach (string archive in Directory.EnumerateFiles(Path.Combine(workingDirectory, ".trykatch/packages"), "*.nupkg", SearchOption.AllDirectories))
                    {
                        using System.IO.Compression.ZipArchive package = System.IO.Compression.ZipFile.OpenRead(archive);
                        using Stream metadata = package.Entries.Single(item => item.Name.EndsWith(".nuspec", StringComparison.Ordinal)).Open();
                        System.Xml.Linq.XDocument nuspec = System.Xml.Linq.XDocument.Load(metadata);
                        string id = nuspec.Descendants().Single(item => item.Name.LocalName == "id").Value.ToLowerInvariant();
                        string version = nuspec.Descendants().Single(item => item.Name.LocalName == "version").Value;
                        string directory = Path.Combine(workingDirectory, cache, id, version);
                        Directory.CreateDirectory(directory);
                        File.Copy(archive, Path.Combine(directory, $"{id}.{version}.nupkg"), overwrite: true);
                    }
                }
            }
            if (fileName == "pnpm" && arguments[0] == "install")
            {
                ManualResetEventSlim? started = Interlocked.Exchange(ref restoreStarted, null);
                ManualResetEventSlim? continuation = Interlocked.Exchange(ref continueRestore, null);
                if (started is not null && continuation is not null)
                {
                    started.Set();
                    continuation.Wait(TimeSpan.FromSeconds(10));
                }
                string packageJsonPath = Path.Combine(workingDirectory, "apps/web/package.json");
                using System.Text.Json.JsonDocument packageJson = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(packageJsonPath));
                foreach (System.Text.Json.JsonProperty dependency in packageJson.RootElement.GetProperty("dependencies").EnumerateObject())
                {
                    string specifier = dependency.Value.GetString()!;
                    if (!specifier.StartsWith("file:", StringComparison.Ordinal)) continue;
                    string archive = Path.GetFullPath(specifier[5..], Path.GetDirectoryName(packageJsonPath)!);
                    byte[] bytes = File.ReadAllBytes(archive);
                    using System.IO.Compression.GZipStream gzip = new(new MemoryStream(bytes), System.IO.Compression.CompressionMode.Decompress);
                    using System.Formats.Tar.TarReader tar = new(gzip);
                    System.Formats.Tar.TarEntry? metadata;
                    do { metadata = tar.GetNextEntry(); } while (metadata is not null && metadata.Name != "package/package.json");
                    using System.Text.Json.JsonDocument identity = System.Text.Json.JsonDocument.Parse(metadata!.DataStream!);
                    string version = identity.RootElement.GetProperty("version").GetString()!;
                    string integrity = "sha512-" + Convert.ToBase64String(SHA512.HashData(bytes));
                    File.WriteAllText(Path.Combine(workingDirectory, "pnpm-lock.yaml"),
                        $"lockfileVersion: '9.0'\n# {dependency.Name}@{version}\nintegrity: {integrity}\n");
                }
            }
            return new(0, "ok");
        }
    }

    private sealed class ActualPackageLifecycleCommandRunner : IWorkspaceCommandRunner
    {
        private readonly RecordingCommandRunner fallback = new();

        public WorkspaceCommandResult Run(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory) =>
            string.Equals(fileName, "pnpm", StringComparison.Ordinal)
            && arguments.Count > 0
            && string.Equals(arguments[0], "install", StringComparison.Ordinal)
                ? new ProcessWorkspaceCommandRunner().Run(fileName, arguments, workingDirectory)
                : fallback.Run(fileName, arguments, workingDirectory);
    }

    private sealed class ArtifactReplacementRunner(string backend, string frontend) : IWorkspaceCommandRunner
    {
        private readonly RecordingCommandRunner restoreRunner = new();

        public WorkspaceCommandResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
        {
            bool verifiesSignature = fileName == "dotnet"
                && arguments.Count > 1
                && arguments[0] == "nuget"
                && arguments[1] == "verify";
            WorkspaceCommandResult result = verifiesSignature
                ? new ProcessWorkspaceCommandRunner().Run(fileName, arguments, workingDirectory)
                : restoreRunner.Run(fileName, arguments, workingDirectory);
            if (result.ExitCode == 0 && verifiesSignature)
            {
                File.WriteAllText(backend, "Changed after verification.");
                File.WriteAllText(frontend, "Changed after verification.");
            }
            return result;
        }
    }
}
