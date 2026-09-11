using Shouldly;
using Trykatch.ModuleTool;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class ApplicationCreatorTests
{
    [TestMethod]
    public async Task CreateUsesTheTemplateEngineAndAllowsThePackagedGitInitializer()
    {
        RecordingApplicationTemplateProcess process = new();
        StringWriter error = new();
        ApplicationCreator creator = new(process, error);

        int exitCode = await creator.CreateAsync(
            ["Horizon", "--ui", "none", "--output", "./products/Horizon"],
            CancellationToken.None);

        exitCode.ShouldBe(0);
        process.Arguments.ShouldBe(
        [
            "new",
            "trykatch",
            "--name",
            "Horizon",
            "--ui",
            "none",
            "--output",
            "./products/Horizon",
            "--allow-scripts",
            "yes"
        ]);
        error.ToString().ShouldBeEmpty();
    }

    [TestMethod]
    [DataRow("--name")]
    [DataRow("-n")]
    [DataRow("--allow-scripts")]
    public async Task CreateRejectsOptionsOwnedByTheCommand(string option)
    {
        RecordingApplicationTemplateProcess process = new();
        StringWriter error = new();
        ApplicationCreator creator = new(process, error);

        int exitCode = await creator.CreateAsync(["Horizon", option, "value"], CancellationToken.None);

        exitCode.ShouldBe(1);
        process.Arguments.ShouldBeNull();
        error.ToString().ShouldContain("managed by 'trykatch new'");
    }

    [TestMethod]
    public async Task CreateRequiresAnApplicationName()
    {
        RecordingApplicationTemplateProcess process = new();
        StringWriter error = new();
        ApplicationCreator creator = new(process, error);

        int exitCode = await creator.CreateAsync([], CancellationToken.None);

        exitCode.ShouldBe(1);
        process.Arguments.ShouldBeNull();
        error.ToString().ShouldContain("Application name is required");
    }

    private sealed class RecordingApplicationTemplateProcess : IApplicationTemplateProcess
    {
        public IReadOnlyList<string>? Arguments { get; private set; }

        public Task<int> RunAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            Arguments = arguments.ToArray();
            return Task.FromResult(0);
        }
    }
}
