using System.Net;
using Shouldly;
using Trykatch.ModuleTool;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class ApplicationDevelopmentTests
{
    [TestMethod]
    public async Task BackendOnlySetupDoesNotRequireFrontendToolsOrDocker()
    {
        using TemporaryDirectory directory = CreateApplication(false);
        RecordingRunner runner = new();
        StringWriter output = new();
        using HttpClient http = new();
        ApplicationDevelopment development = new(runner, new UnavailableDocker(), http, output);
        (await development.SetupAsync(directory.Path, CancellationToken.None)).ShouldBe(0);
        runner.Commands.Count.ShouldBe(2);
        runner.Commands.ShouldAllBe(command => command.Executable == "dotnet");
        runner.Commands[1].Arguments.ShouldContain("--locked-mode");
        output.ToString().ShouldContain("No secrets, certificates or databases were changed");
    }

    [TestMethod]
    public async Task SetupRejectsPathPnpmMismatchBeforeRestoring()
    {
        using TemporaryDirectory directory = CreateApplication(true);
        RecordingRunner runner = new() { PathPnpmVersion = "11.0.0" };
        using HttpClient http = new();
        StringWriter output = new();
        ApplicationDevelopment development = new(runner, new UnavailableDocker(), http, output);
        (await development.SetupAsync(directory.Path, CancellationToken.None)).ShouldBe(2);
        runner.Commands.ShouldNotContain(command => command.Arguments.Contains("restore") || command.Arguments.Contains("install"));
        output.ToString().ShouldContain("pnpm on PATH (used by Aspire)");
        output.ToString().ShouldContain("10.17.1");
    }

    [TestMethod]
    public async Task SetupInstallsFrozenFrontendDependenciesAndStopsOnRestoreFailure()
    {
        using TemporaryDirectory directory = CreateApplication(true);
        RecordingRunner runner = new();
        using HttpClient http = new();
        ApplicationDevelopment development = new(runner, new UnavailableDocker(), http, new StringWriter());
        (await development.SetupAsync(directory.Path, CancellationToken.None)).ShouldBe(0);
        runner.Commands.Last().Executable.ShouldBe("corepack");
        runner.Commands.Last().Arguments.ShouldBe(["pnpm", "install", "--frozen-lockfile"]);
        runner.Commands.Last().Root.ShouldBe(Path.Combine(directory.Path, "web"));
        runner.Commands.Clear();
        runner.RestoreExitCode = 1;
        (await development.SetupAsync(directory.Path, CancellationToken.None)).ShouldBe(2);
        runner.Commands.ShouldNotContain(command => command.Arguments.Contains("install"));
    }

    [TestMethod]
    [DataRow("https://example.com")]
    [DataRow("http://user:pass@localhost")]
    [DataRow("http://localhost/path")]
    [DataRow("http://localhost?token=secret")]
    [DataRow("file:///tmp")]
    public void StatusRejectsNonlocalOrSensitiveUrls(string url) =>
        Should.Throw<ArgumentException>(() => ApplicationDevelopment.ValidateLocalApiUrl(url));

    [TestMethod]
    public async Task StatusWithoutUrlDoesNotPretendTheApplicationIsRunning()
    {
        using TemporaryDirectory directory = CreateApplication(false);
        RecordingHealthHandler handler = new(HttpStatusCode.OK);
        using HttpClient http = new(handler);
        StringWriter output = new();
        ApplicationDevelopment development = new(new RecordingRunner(), new UnavailableDocker(), http, output);
        (await development.StatusAsync(directory.Path, null, CancellationToken.None)).ShouldBe(0);
        handler.Paths.ShouldBeEmpty();
        output.ToString().ShouldContain("Runtime health: unknown");
    }

    [TestMethod]
    public async Task StatusUsesLiveAndReadyAndDoesNotAcceptRedirects()
    {
        using TemporaryDirectory directory = CreateApplication(false);
        RecordingHealthHandler handler = new(HttpStatusCode.Redirect);
        using HttpClient http = new(handler);
        ApplicationDevelopment development = new(new RecordingRunner(), new UnavailableDocker(), http, new StringWriter());
        (await development.StatusAsync(directory.Path, "http://127.0.0.1:5274", CancellationToken.None)).ShouldBe(2);
        handler.Paths.ShouldBe(["/health/live", "/health/ready"]);
    }

    [TestMethod]
    public async Task DoctorReportsEveryProblemWithoutRepairingTheWorkspace()
    {
        using TemporaryDirectory directory = CreateApplication(true);
        RecordingRunner runner = new() { NodeVersion = "v22.0.0", PathPnpmVersion = "11.0.0" };
        using HttpClient http = new();
        ApplicationDevelopment development = new(runner, new UnavailableDocker(), http, new StringWriter());
        DevelopmentReport report = await development.DoctorAsync(directory.Path, CancellationToken.None);
        report.IsReady.ShouldBeFalse();
        report.Checks.Single(check => check.Name == "Node.js").Passed.ShouldBeFalse();
        report.Checks.Single(check => check.Name == "Docker daemon").Passed.ShouldBeFalse();
        report.Checks.Single(check => check.Name == "module graph").Passed.ShouldBeFalse();
        runner.Commands.ShouldNotContain(command => command.Arguments.Contains("install") || command.Arguments.Contains("restore") || command.Arguments.Contains("--trust"));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.OK, 0)]
    [DataRow(HttpStatusCode.ServiceUnavailable, 2)]
    public async Task StatusReflectsActualHealthResponses(HttpStatusCode status, int expectedExit)
    {
        using TemporaryDirectory directory = CreateApplication(false);
        using HttpClient http = new(new RecordingHealthHandler(status));
        ApplicationDevelopment development = new(new RecordingRunner(), new UnavailableDocker(), http, new StringWriter());
        (await development.StatusAsync(directory.Path, "http://localhost:5274", CancellationToken.None)).ShouldBe(expectedExit);
    }

    [TestMethod]
    public async Task SetupHonorsCancellationBeforeAnyWrites()
    {
        using TemporaryDirectory directory = CreateApplication(false);
        RecordingRunner runner = new();
        using HttpClient http = new();
        ApplicationDevelopment development = new(runner, new UnavailableDocker(), http, new StringWriter());
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => development.SetupAsync(directory.Path, cancellation.Token));
        runner.Commands.ShouldBeEmpty();
    }

    private static TemporaryDirectory CreateApplication(bool web)
    {
        TemporaryDirectory directory = new();
        string host = Directory.CreateDirectory(Path.Combine(directory.Path, "src", "Horizon.AppHost")).FullName;
        File.WriteAllText(Path.Combine(host, "Horizon.AppHost.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(directory.Path, "Horizon.slnx"), "<Solution />");
        if (web)
        {
            string path = Directory.CreateDirectory(Path.Combine(directory.Path, "web")).FullName;
            File.WriteAllText(Path.Combine(path, "package.json"), "{\"packageManager\":\"pnpm@10.17.1\"}");
        }
        return directory;
    }

    private sealed class RecordingRunner : IWorkspaceCommandRunner
    {
        public List<(string Executable, IReadOnlyList<string> Arguments, string Root)> Commands { get; } = [];
        public string PathPnpmVersion { get; set; } = "10.17.1";
        public string NodeVersion { get; set; } = "v24.18.1";
        public int RestoreExitCode { get; set; }
        public WorkspaceCommandResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
        {
            Commands.Add((fileName, arguments, workingDirectory));
            if (arguments.Contains("restore")) return new(RestoreExitCode, "restore result");
            return new(0, fileName switch { "dotnet" => "10.0.301", "node" => NodeVersion, "pnpm" => PathPnpmVersion, _ => "10.17.1" });
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("trykatch-development-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class UnavailableDocker : IContainerRuntimeProbe
    {
        public Task<ContainerRuntimeStatus> CheckAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ContainerRuntimeStatus.Unavailable("Not running"));
    }

    private sealed class RecordingHealthHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
