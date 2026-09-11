using Shouldly;
using Trykatch.ModuleTool;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class TemplatePackageInstallerTests
{
    [TestMethod]
    public void CurrentVersionMatchesTheCliPackageVersion()
    {
        TemplatePackageInstaller.CurrentVersion.ShouldBe("0.1.0-preview.11");
    }

    [TestMethod]
    public async Task InstallUsesTheOfficialTemplateEngineAndReportsCompletion()
    {
        RecordingTemplateEngine engine = new(new(0, "installed", string.Empty));
        StringWriter output = new();
        StringWriter error = new();
        TemplatePackageInstaller installer = new(engine, output, error, isInteractive: false);

        int exitCode = await installer.InstallAsync("0.1.0-preview.11", force: true, CancellationToken.None);

        exitCode.ShouldBe(0);
        engine.Package.ShouldBe("Trykatch.Templates@0.1.0-preview.11");
        engine.Force.ShouldBeTrue();
        output.ToString().ShouldContain("Installing Trykatch template 0.1.0-preview.11");
        output.ToString().ShouldContain("Trykatch template 0.1.0-preview.11 installed");
        output.ToString().ShouldContain("dotnet new trykatch -n <name>");
        error.ToString().ShouldBeEmpty();
    }

    [TestMethod]
    public async Task InstallTreatsTheRequestedVersionAlreadyBeingInstalledAsSuccess()
    {
        RecordingTemplateEngine engine = new(new(106, string.Empty, "Le modèle est déjà installé."))
        {
            IsRequestedVersionInstalled = true
        };
        StringWriter output = new();
        StringWriter error = new();
        TemplatePackageInstaller installer = new(engine, output, error, isInteractive: false);

        int exitCode = await installer.InstallAsync("0.1.0-preview.11", force: false, CancellationToken.None);

        exitCode.ShouldBe(0);
        engine.Package.ShouldBeNull();
        output.ToString().ShouldContain("Trykatch template 0.1.0-preview.11 is already installed");
        error.ToString().ShouldBeEmpty();
    }

    [TestMethod]
    public async Task DotnetTemplateEngineReadsInstalledVersionFromTheTemplateRegistry()
    {
        string templateEngineHome = Path.Combine(Path.GetTempPath(), $"trykatch-template-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(templateEngineHome);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(templateEngineHome, "packages.json"),
                """
                {
                  "Packages": [
                    {
                      "Details": {
                        "PackageId": "Trykatch.Templates",
                        "Version": "0.1.0-preview.11"
                      }
                    }
                  ]
                }
                """);
            DotnetTemplateEngine engine = new(templateEngineHome);

            bool installed = await engine.IsPackageInstalledAsync(
                "Trykatch.Templates",
                "0.1.0-preview.11",
                CancellationToken.None);
            bool otherVersionInstalled = await engine.IsPackageInstalledAsync(
                "Trykatch.Templates",
                "0.1.0-preview.8",
                CancellationToken.None);

            installed.ShouldBeTrue();
            otherVersionInstalled.ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(templateEngineHome, recursive: true);
        }
    }

    [TestMethod]
    public async Task UpdateReplacesTheInstalledTemplateWithTheRequestedVersion()
    {
        RecordingTemplateEngine engine = new(new(0, "updated", string.Empty));
        StringWriter output = new();
        StringWriter error = new();
        TemplatePackageInstaller installer = new(engine, output, error, isInteractive: false);

        int exitCode = await installer.UpdateAsync("0.1.0-preview.11", CancellationToken.None);

        exitCode.ShouldBe(0);
        engine.Package.ShouldBe("Trykatch.Templates@0.1.0-preview.11");
        engine.Force.ShouldBeTrue();
        output.ToString().ShouldContain("Updating Trykatch template to 0.1.0-preview.11");
        output.ToString().ShouldContain("Trykatch template updated to 0.1.0-preview.11");
        error.ToString().ShouldBeEmpty();
    }

    [TestMethod]
    public async Task UpdatePreservesTheTemplateEngineFailureDetails()
    {
        RecordingTemplateEngine engine = new(new(103, string.Empty, "The package does not exist."));
        StringWriter error = new();
        TemplatePackageInstaller installer = new(engine, TextWriter.Null, error, isInteractive: false);

        int exitCode = await installer.UpdateAsync("0.1.0-preview.109", CancellationToken.None);

        exitCode.ShouldBe(103);
        engine.Force.ShouldBeTrue();
        error.ToString().ShouldContain("Could not update Trykatch template to 0.1.0-preview.109");
        error.ToString().ShouldContain("The package does not exist.");
    }

    [TestMethod]
    public async Task UninstallUsesTheOfficialTemplateEngineAndExplainsCliRemoval()
    {
        RecordingTemplateEngine engine = new(new(0, "uninstalled", string.Empty));
        StringWriter output = new();
        StringWriter error = new();
        TemplatePackageInstaller installer = new(engine, output, error, isInteractive: false);

        int exitCode = await installer.UninstallAsync(CancellationToken.None);

        exitCode.ShouldBe(0);
        engine.UninstalledPackageId.ShouldBe("Trykatch.Templates");
        output.ToString().ShouldContain("Trykatch template uninstalled");
        output.ToString().ShouldContain("dotnet tool uninstall --global Trykatch.Cli");
        error.ToString().ShouldBeEmpty();
    }

    [TestMethod]
    public async Task InstallPreservesTheTemplateEngineFailureDetails()
    {
        RecordingTemplateEngine engine = new(new(103, string.Empty, "The package does not exist."));
        StringWriter output = new();
        StringWriter error = new();
        TemplatePackageInstaller installer = new(engine, output, error, isInteractive: false);

        int exitCode = await installer.InstallAsync("0.1.0-preview.109", force: false, CancellationToken.None);

        exitCode.ShouldBe(103);
        output.ToString().ShouldContain("Installing Trykatch template 0.1.0-preview.109");
        error.ToString().ShouldContain("Could not install Trykatch template 0.1.0-preview.109");
        error.ToString().ShouldContain("The package does not exist.");
    }

    [TestMethod]
    public async Task InstallDoesNotHideAnUnrelatedTemplateEngineExitCode106Failure()
    {
        RecordingTemplateEngine engine = new(new(106, string.Empty, "No valid NuGet feeds are configured."));
        StringWriter error = new();
        TemplatePackageInstaller installer = new(engine, TextWriter.Null, error, isInteractive: false);

        int exitCode = await installer.InstallAsync("0.1.0-preview.109", force: false, CancellationToken.None);

        exitCode.ShouldBe(106);
        error.ToString().ShouldContain("Could not install Trykatch template 0.1.0-preview.109");
        error.ToString().ShouldContain("No valid NuGet feeds are configured.");
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("preview.6")]
    [DataRow("0.1.0 preview.6")]
    public async Task InstallRejectsAnInvalidVersionBeforeStartingDotnet(string version)
    {
        RecordingTemplateEngine engine = new(new(0, string.Empty, string.Empty));
        StringWriter error = new();
        TemplatePackageInstaller installer = new(engine, TextWriter.Null, error, isInteractive: false);

        int exitCode = await installer.InstallAsync(version, force: false, CancellationToken.None);

        exitCode.ShouldBe(1);
        engine.Package.ShouldBeNull();
        error.ToString().ShouldContain("valid semantic version");
    }

    private sealed class RecordingTemplateEngine(TemplateEngineResult result) : ITemplateEngine
    {
        public bool IsRequestedVersionInstalled { get; init; }

        public string? Package { get; private set; }

        public bool Force { get; private set; }

        public string? UninstalledPackageId { get; private set; }

        public Task<bool> IsPackageInstalledAsync(
            string packageId,
            string version,
            CancellationToken cancellationToken) =>
            Task.FromResult(IsRequestedVersionInstalled);

        public Task<TemplateEngineResult> InstallAsync(
            string package,
            bool force,
            CancellationToken cancellationToken)
        {
            Package = package;
            Force = force;
            return Task.FromResult(result);
        }

        public Task<TemplateEngineResult> UninstallAsync(
            string packageId,
            CancellationToken cancellationToken)
        {
            UninstalledPackageId = packageId;
            return Task.FromResult(result);
        }
    }
}
