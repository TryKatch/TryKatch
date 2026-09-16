using Shouldly;
using Trykatch.ModuleTool;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class ModuleCreationConsoleProgressTests
{
    [TestMethod]
    public void SharedRendererUsesTheOperationTitleInsteadOfAssumingModuleCreation()
    {
        using StringWriter output = new();
        using CliOperationProgress progress = new(output, interactive: false, "Application generation");
        progress.ReportStep("Running application template and packaged Git setup");
        progress.Complete();
        output.ToString().ShouldContain("Application generation completed in ");
        output.ToString().ShouldNotContain("Module generation");
    }

    [TestMethod]
    public void RedirectedOutputHasOrderedStepsWithoutTerminalControlCharacters()
    {
        using StringWriter output = new();
        using CliOperationProgress progress = new(output, interactive: false);
        progress.Report(new("Validating workspace"));
        progress.Report(new("Building backend"));
        progress.Complete();

        string log = output.ToString();
        log.ShouldContain("[1] Validating workspace...");
        log.ShouldContain("OK [1] Validating workspace (");
        log.ShouldContain("[2] Building backend...");
        log.ShouldContain("OK [2] Building backend (");
        log.ShouldContain("Module generation completed in ");
        log.Replace(Environment.NewLine, "\n", StringComparison.Ordinal).ShouldNotContain("\r");
        log.ShouldNotContain("\u001b");
    }

    [TestMethod]
    public void InteractiveOutputAnimatesAndStopsAfterCompletion()
    {
        using StringWriter output = new();
        using CliOperationProgress progress = new(output, interactive: true);
        progress.Report(new("Building backend"));
        progress.Refresh();
        progress.Complete();
        string completed = output.ToString();
        progress.Refresh();
        progress.Report(new("Must not appear"));
        progress.Complete();

        output.ToString().ShouldBe(completed);
        completed.ShouldContain("| [1] Building backend...");
        completed.ShouldContain("/ [1] Building backend...");
        completed.ShouldContain("OK [1]");
    }

    [TestMethod]
    public void RollbackDoesNotMarkTheFailedStepAsSuccessful()
    {
        using StringWriter output = new();
        using CliOperationProgress progress = new(output, interactive: false);
        progress.Report(new("Building backend"));
        progress.Report(new("Restoring the original workspace", IsRollback: true));
        progress.Fail();

        string log = output.ToString();
        log.ShouldContain("FAIL [1] Building backend");
        log.ShouldContain("[2] Restoring the original workspace...");
        log.ShouldContain("Module generation stopped after ");
        log.ShouldNotContain("OK");
        log.ShouldNotContain("completed in");
    }

    [TestMethod]
    public void CancellationBeforeMutationHasNoSuccessSummary()
    {
        using StringWriter output = new();
        using CliOperationProgress progress = new(output, interactive: false);
        progress.Report(new("Waiting for the workspace lock"));
        progress.Fail();
        output.ToString().ShouldContain("FAIL [1] Waiting for the workspace lock");
        output.ToString().ShouldNotContain("completed in");
    }

    [TestMethod]
    public void DisposingClearsTheSpinnerAndPreventsFurtherOutput()
    {
        using StringWriter output = new();
        CliOperationProgress progress = new(output, interactive: true);
        progress.Report(new("Building backend"));
        progress.Dispose();
        string disposed = output.ToString();
        progress.Refresh();
        progress.Report(new("Must not appear"));
        progress.Dispose();
        output.ToString().ShouldBe(disposed);
    }

    [TestMethod]
    public void ClosedOutputDoesNotInterruptGenerationOrRollback()
    {
        using StringWriter output = new();
        using CliOperationProgress progress = new(output, interactive: true);
        output.Close();
        progress.Report(new("Building backend"));
        progress.Refresh();
        progress.Report(new("Restoring the original workspace", IsRollback: true));
        progress.Fail();
    }
}
