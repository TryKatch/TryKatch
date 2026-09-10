using Shouldly;
using TrykatchApp.ModuleTool;

namespace TrykatchApp.UnitTests;

[TestClass]
public sealed class ApplicationStarterTests
{
    [TestMethod]
    public async Task StartDiscoversTheAppHostFromANestedDirectory()
    {
        using TemporaryDirectory temporaryDirectory = new();
        string appHostDirectory = Directory.CreateDirectory(
            Path.Combine(temporaryDirectory.Path, "src", "Horizon.AppHost")).FullName;
        string appHostProject = Path.Combine(appHostDirectory, "Horizon.AppHost.csproj");
        await File.WriteAllTextAsync(appHostProject, "<Project />");
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory.Path, "Horizon.slnx"), "<Solution />");
        string nestedDirectory = Directory.CreateDirectory(
            Path.Combine(temporaryDirectory.Path, "src", "Horizon.Api", "Features")).FullName;
        RecordingApplicationProcess process = new();
        StringWriter output = new();
        ApplicationStarter starter = new(process, output);

        int exitCode = await starter.StartAsync(nestedDirectory, CancellationToken.None);

        exitCode.ShouldBe(0);
        process.ProjectPath.ShouldBe(appHostProject);
        process.WorkingDirectory.ShouldBe(temporaryDirectory.Path);
        output.ToString().ShouldContain("Starting Horizon through its Aspire AppHost");
    }

    [TestMethod]
    public void DiscoverRejectsAnAmbiguousApplication()
    {
        using TemporaryDirectory temporaryDirectory = new();
        CreateAppHost(temporaryDirectory.Path, "One");
        CreateAppHost(temporaryDirectory.Path, "Two");

        Should.Throw<InvalidOperationException>(() => ApplicationLocation.Discover(temporaryDirectory.Path))
            .Message.ShouldContain("More than one Aspire AppHost");
    }

    [TestMethod]
    public void DiscoverRejectsADirectoryWithoutAnAppHost()
    {
        using TemporaryDirectory temporaryDirectory = new();

        Should.Throw<InvalidOperationException>(() => ApplicationLocation.Discover(temporaryDirectory.Path))
            .Message.ShouldContain("No '*.AppHost.csproj'");
    }

    [TestMethod]
    public void DotnetProcessUsesTheHttpsAppHostLaunchProfile()
    {
        string projectPath = Path.GetFullPath("src/Horizon.AppHost/Horizon.AppHost.csproj");
        string workingDirectory = Path.GetFullPath(".");

        System.Diagnostics.ProcessStartInfo startInfo =
            DotnetApplicationProcess.CreateStartInfo(projectPath, workingDirectory);

        startInfo.FileName.ShouldBe("dotnet");
        startInfo.WorkingDirectory.ShouldBe(workingDirectory);
        startInfo.UseShellExecute.ShouldBeFalse();
        startInfo.ArgumentList.ShouldBe(["run", "--launch-profile", "https", "--project", projectPath]);
    }

    private static void CreateAppHost(string root, string name)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, "src", $"{name}.AppHost")).FullName;
        File.WriteAllText(Path.Combine(directory, $"{name}.AppHost.csproj"), "<Project />");
    }

    private sealed class RecordingApplicationProcess : IApplicationProcess
    {
        public string? ProjectPath { get; private set; }

        public string? WorkingDirectory { get; private set; }

        public Task<int> RunAsync(
            string projectPath,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            ProjectPath = projectPath;
            WorkingDirectory = workingDirectory;
            return Task.FromResult(0);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"trykatch-start-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
