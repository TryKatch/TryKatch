using Shouldly;
using TrykatchApp.ModuleTool;

namespace TrykatchApp.UnitTests;

[TestClass]
public sealed class TemplatePackageInstallerTests
{
    [TestMethod]
    public void CurrentVersionMatchesTheCliPackageVersion()
    {
        TemplatePackageInstaller.CurrentVersion.ShouldBe("0.1.0-preview.6");
    }

    [TestMethod]
    public async Task InstallUsesTheOfficialTemplateEngineAndReportsCompletion()
    {
        RecordingTemplateEngine engine = new(new(0, "installed", string.Empty));
        StringWriter output = new();
        StringWriter error = new();
        TemplatePackageInstaller installer = new(engine, output, error, isInteractive: false);

        int exitCode = await installer.InstallAsync("0.1.0-preview.6", force: true, CancellationToken.None);

        exitCode.ShouldBe(0);
        engine.Package.ShouldBe("Trykatch.Templates@0.1.0-preview.6");
        engine.Force.ShouldBeTrue();
        output.ToString().ShouldContain("Installing Trykatch template 0.1.0-preview.6");
        output.ToString().ShouldContain("Trykatch template 0.1.0-preview.6 installed");
        output.ToString().ShouldContain("dotnet new trykatch -n <name>");
        error.ToString().ShouldBeEmpty();
    }

    [TestMethod]
    public async Task InstallPreservesTheTemplateEngineFailureDetails()
    {
        RecordingTemplateEngine engine = new(new(103, string.Empty, "The package does not exist."));
        StringWriter output = new();
        StringWriter error = new();
        TemplatePackageInstaller installer = new(engine, output, error, isInteractive: false);

        int exitCode = await installer.InstallAsync("0.1.0-preview.99", force: false, CancellationToken.None);

        exitCode.ShouldBe(103);
        output.ToString().ShouldContain("Installing Trykatch template 0.1.0-preview.99");
        error.ToString().ShouldContain("Could not install Trykatch template 0.1.0-preview.99");
        error.ToString().ShouldContain("The package does not exist.");
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
        public string? Package { get; private set; }

        public bool Force { get; private set; }

        public Task<TemplateEngineResult> InstallAsync(
            string package,
            bool force,
            CancellationToken cancellationToken)
        {
            Package = package;
            Force = force;
            return Task.FromResult(result);
        }
    }
}
