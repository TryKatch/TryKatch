using Shouldly;
using Trykatch.ModuleTool;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class ApplicationCreatorTests
{
    [TestMethod]
    public void ExistingFileConflictsHaveAConciseActionableDefault()
    {
        ApplicationTemplateResult result = new(73, "Overwrite existing/file1.cs\nOverwrite existing/file2.cs");
        string output = ApplicationCreationOutput.Format(result, verbose: false);
        output.ShouldContain("refused to overwrite existing files");
        output.ShouldContain("--output");
        output.ShouldContain("--verbose");
        output.ShouldNotContain("existing/file1.cs");
    }

    [TestMethod]
    public void VerboseConflictsAndOtherFailuresRetainTheOriginalDiagnostics()
    {
        ApplicationTemplateResult conflict = new(73, "Overwrite existing/file1.cs\nOverwrite existing/file2.cs");
        ApplicationCreationOutput.Format(conflict, verbose: true).ShouldBe(conflict.Output);
        ApplicationTemplateResult otherFailure = new(103, "Trykatch template is not installed.");
        ApplicationCreationOutput.Format(otherFailure, verbose: false).ShouldBe(otherFailure.Output);
    }

    [TestMethod]
    public async Task ProgressIsReportedBeforeTheTemplateEngineFinishes()
    {
        DeferredApplicationTemplateProcess process = new();
        List<string> steps = [];
        ApplicationCreator creator = new(process, steps.Add);
        Task<ApplicationTemplateResult> creation = creator.CreateAsync(["Horizon"], CancellationToken.None);

        steps.ShouldBe(["Validating application options", "Running application template and packaged Git setup"]);
        creation.IsCompleted.ShouldBeFalse();
        process.Completion.SetResult(new(0, "Application created and Git initialized."));
        ApplicationTemplateResult result = await creation;
        result.Output.ShouldBe("Application created and Git initialized.");
        result.ExitCode.ShouldBe(0);
    }

    [TestMethod]
    public async Task InvalidOptionsReportOnlyValidationAndDoNotRunTheEngine()
    {
        RecordingApplicationTemplateProcess process = new();
        List<string> steps = [];
        ApplicationCreator creator = new(process, steps.Add);
        ApplicationTemplateResult result = await creator.CreateAsync(["Horizon", "--name", "Other"], CancellationToken.None);
        steps.ShouldBe(["Validating application options"]);
        process.Arguments.ShouldBeNull();
        result.ExitCode.ShouldBe(1);
    }

    [TestMethod]
    public async Task TemplateEngineFailuresPreserveDiagnosticsAndExitCode()
    {
        DeferredApplicationTemplateProcess process = new();
        ApplicationCreator creator = new(process);
        process.Completion.SetResult(new(73, "Output files already exist; no files overwritten."));
        ApplicationTemplateResult result = await creator.CreateAsync(["Horizon"], CancellationToken.None);
        result.ExitCode.ShouldBe(73);
        result.Output.ShouldContain("no files overwritten");
    }

    [TestMethod]
    public async Task CancellationIsNotConvertedToSuccessfulCreation()
    {
        DeferredApplicationTemplateProcess process = new();
        ApplicationCreator creator = new(process);
        process.Completion.SetCanceled();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await creator.CreateAsync(["Horizon"], CancellationToken.None));
    }

    [TestMethod]
    public async Task CreateUsesTheTemplateEngineAndAllowsThePackagedGitInitializer()
    {
        RecordingApplicationTemplateProcess process = new();
        ApplicationCreator creator = new(process);

        ApplicationTemplateResult result = await creator.CreateAsync(
            ["Horizon", "--ui", "none", "--output", "./products/Horizon"],
            CancellationToken.None);

        result.ExitCode.ShouldBe(0);
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
        result.Output.ShouldBeEmpty();
    }

    [TestMethod]
    [DataRow("--name")]
    [DataRow("-n")]
    [DataRow("--allow-scripts")]
    public async Task CreateRejectsOptionsOwnedByTheCommand(string option)
    {
        RecordingApplicationTemplateProcess process = new();
        ApplicationCreator creator = new(process);

        ApplicationTemplateResult result = await creator.CreateAsync(["Horizon", option, "value"], CancellationToken.None);

        result.ExitCode.ShouldBe(1);
        process.Arguments.ShouldBeNull();
        result.Output.ShouldContain("managed by 'trykatch new'");
    }

    [TestMethod]
    public async Task CreateRequiresAnApplicationName()
    {
        RecordingApplicationTemplateProcess process = new();
        ApplicationCreator creator = new(process);

        ApplicationTemplateResult result = await creator.CreateAsync([], CancellationToken.None);

        result.ExitCode.ShouldBe(1);
        process.Arguments.ShouldBeNull();
        result.Output.ShouldContain("Application name is required");
    }

    private sealed class RecordingApplicationTemplateProcess : IApplicationTemplateProcess
    {
        public IReadOnlyList<string>? Arguments { get; private set; }

        public Task<ApplicationTemplateResult> RunAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            Arguments = arguments.ToArray();
            return Task.FromResult(new ApplicationTemplateResult(0, string.Empty));
        }
    }

    private sealed class DeferredApplicationTemplateProcess : IApplicationTemplateProcess
    {
        public TaskCompletionSource<ApplicationTemplateResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ApplicationTemplateResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
            Completion.Task;
    }
}
