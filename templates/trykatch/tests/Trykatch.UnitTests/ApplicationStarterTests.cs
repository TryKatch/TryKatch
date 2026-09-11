using Shouldly;
using Trykatch.ModuleTool;

namespace Trykatch.UnitTests;

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
        RecordingContainerRuntimeProbe runtime = new(ContainerRuntimeStatus.Ready("28.4.0", "28.4.0"));
        StringWriter output = new();
        ApplicationStarter starter = new(process, runtime, output);

        int exitCode = await starter.StartAsync(nestedDirectory, CancellationToken.None);

        exitCode.ShouldBe(0);
        process.ProjectPath.ShouldBe(appHostProject);
        process.WorkingDirectory.ShouldBe(temporaryDirectory.Path);
        runtime.CheckCount.ShouldBe(1);
        output.ToString().ShouldContain("Docker 28.4.0 is ready");
        output.ToString().ShouldContain("Starting Horizon through its Aspire AppHost");
    }

    [TestMethod]
    public async Task StartStopsBeforeAspireWhenDockerIsUnavailable()
    {
        using TemporaryDirectory temporaryDirectory = new();
        CreateAppHost(temporaryDirectory.Path, "Horizon");
        RecordingApplicationProcess process = new();
        RecordingContainerRuntimeProbe runtime = new(ContainerRuntimeStatus.Unavailable(
            "Docker Desktop is installed, but its engine did not answer 'docker version'."));
        StringWriter output = new();
        ApplicationStarter starter = new(process, runtime, output);

        int exitCode = await starter.StartAsync(temporaryDirectory.Path, CancellationToken.None);

        exitCode.ShouldBe(2);
        process.ProjectPath.ShouldBeNull();
        output.ToString().ShouldContain("Trykatch stopped before Aspire");
        output.ToString().ShouldContain("docker info");
    }

    [TestMethod]
    public async Task StartRejectsADockerClientThatAspireDoesNotSupport()
    {
        using TemporaryDirectory temporaryDirectory = new();
        CreateAppHost(temporaryDirectory.Path, "Horizon");
        RecordingApplicationProcess process = new();
        RecordingContainerRuntimeProbe runtime = new(ContainerRuntimeStatus.UnsupportedClient("23.0.6"));
        StringWriter output = new();
        ApplicationStarter starter = new(process, runtime, output);

        int exitCode = await starter.StartAsync(temporaryDirectory.Path, CancellationToken.None);

        exitCode.ShouldBe(2);
        process.ProjectPath.ShouldBeNull();
        output.ToString().ShouldContain("Docker CLI 25.0 or newer");
        output.ToString().ShouldContain("23.0.6");
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

    [TestMethod]
    public void DockerProbeChecksBothTheClientAndDaemonVersions()
    {
        System.Diagnostics.ProcessStartInfo startInfo = DockerContainerRuntimeProbe.CreateStartInfo();

        startInfo.FileName.ShouldBe("docker");
        startInfo.UseShellExecute.ShouldBeFalse();
        startInfo.RedirectStandardOutput.ShouldBeTrue();
        startInfo.RedirectStandardError.ShouldBeTrue();
        startInfo.ArgumentList.ShouldBe([
            "version",
            "--format",
            "{{.Client.Version}}|{{.Server.Version}}"
        ]);
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

    private sealed class RecordingContainerRuntimeProbe(ContainerRuntimeStatus result) : IContainerRuntimeProbe
    {
        public int CheckCount { get; private set; }

        public Task<ContainerRuntimeStatus> CheckAsync(CancellationToken cancellationToken)
        {
            CheckCount++;
            return Task.FromResult(result);
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
