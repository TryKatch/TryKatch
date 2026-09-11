using System.Diagnostics;

namespace Trykatch.ModuleTool;

internal sealed class ApplicationStarter(
    IApplicationProcess applicationProcess,
    IContainerRuntimeProbe containerRuntimeProbe,
    TextWriter output)
{
    public async Task<int> StartAsync(string root, CancellationToken cancellationToken)
    {
        ApplicationLocation location = ApplicationLocation.Discover(root);
        await output.WriteLineAsync("Checking Docker container runtime...");
        ContainerRuntimeStatus runtime = await containerRuntimeProbe.CheckAsync(cancellationToken);
        if (!runtime.IsReady)
        {
            await output.WriteLineAsync("Trykatch stopped before Aspire because the container runtime is not ready.");
            await output.WriteLineAsync(runtime.Message);
            await output.WriteLineAsync("Start or update Docker Desktop, then wait until 'docker info' succeeds and run 'trykatch start' again.");
            return 2;
        }

        await output.WriteLineAsync($"Docker {runtime.ServerVersion} is ready (CLI {runtime.ClientVersion}).");
        await output.WriteLineAsync($"Starting {location.Name} through its Aspire AppHost...");
        await output.WriteLineAsync("Docker must remain available while the application is running.");

        return await applicationProcess.RunAsync(
            location.ProjectPath,
            location.WorkingDirectory,
            cancellationToken);
    }
}

internal sealed record ContainerRuntimeStatus(
    bool IsReady,
    string? ClientVersion,
    string? ServerVersion,
    string Message)
{
    public static ContainerRuntimeStatus Ready(string clientVersion, string serverVersion) =>
        new(true, clientVersion, serverVersion, string.Empty);

    public static ContainerRuntimeStatus UnsupportedClient(string clientVersion) =>
        new(false, clientVersion, null,
            $"Docker CLI 25.0 or newer is required by Aspire; the active CLI is {clientVersion}. Update Docker Desktop or correct the PATH used by this terminal or IDE.");

    public static ContainerRuntimeStatus Unavailable(string message) =>
        new(false, null, null, message);
}

internal interface IContainerRuntimeProbe
{
    Task<ContainerRuntimeStatus> CheckAsync(CancellationToken cancellationToken);
}

internal sealed class DockerContainerRuntimeProbe : IContainerRuntimeProbe
{
    private const int MinimumDockerClientMajorVersion = 25;
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(6);

    public async Task<ContainerRuntimeStatus> CheckAsync(CancellationToken cancellationToken)
    {
        ContainerRuntimeStatus lastFailure = ContainerRuntimeStatus.Unavailable(
            "Docker did not answer the runtime readiness check.");

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            lastFailure = await CheckOnceAsync(cancellationToken);
            if (lastFailure.IsReady || lastFailure.ClientVersion is not null)
                return lastFailure;

            if (attempt < 3)
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
        }

        return lastFailure;
    }

    private static async Task<ContainerRuntimeStatus> CheckOnceAsync(CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = CreateStartInfo();
        try
        {
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The Docker CLI process could not be started.");
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(AttemptTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryTerminate(process);
                await Task.WhenAll(standardOutput, standardError);
                return ContainerRuntimeStatus.Unavailable(
                    "Docker Desktop did not answer within six seconds. It may still be waking from Resource Saver.");
            }

            string output = (await standardOutput).Trim();
            string error = (await standardError).Trim();
            if (process.ExitCode != 0)
                return ContainerRuntimeStatus.Unavailable(DescribeFailure(error));

            string[] versions = output.Split('|', 2, StringSplitOptions.TrimEntries);
            if (versions.Length != 2 || !TryReadMajorVersion(versions[0], out int clientMajor))
                return ContainerRuntimeStatus.Unavailable(
                    $"Docker returned an unexpected version response: '{output}'. Run 'docker version' to inspect the active client and daemon.");
            if (clientMajor < MinimumDockerClientMajorVersion)
                return ContainerRuntimeStatus.UnsupportedClient(versions[0]);

            return ContainerRuntimeStatus.Ready(versions[0], versions[1]);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return ContainerRuntimeStatus.Unavailable(
                "The Docker CLI was not found. Install Docker Desktop or make the 'docker' command available on PATH.");
        }
    }

    internal static ProcessStartInfo CreateStartInfo()
    {
        ProcessStartInfo startInfo = new("docker")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("version");
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add("{{.Client.Version}}|{{.Server.Version}}");
        return startInfo;
    }

    private static bool TryReadMajorVersion(string version, out int major)
    {
        int separator = version.IndexOf('.');
        ReadOnlySpan<char> majorPart = separator >= 0 ? version.AsSpan(0, separator) : version.AsSpan();
        return int.TryParse(majorPart, out major);
    }

    private static string DescribeFailure(string error)
    {
        string detail = string.IsNullOrWhiteSpace(error)
            ? "The Docker daemon did not accept the readiness check."
            : error.ReplaceLineEndings(" ");
        return $"Docker is installed but its daemon is unavailable. {detail}";
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited between the state check and termination.
        }
    }
}

internal sealed record ApplicationLocation(string Name, string ProjectPath, string WorkingDirectory)
{
    public static ApplicationLocation Discover(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        string fullPath = Path.GetFullPath(root);
        if (File.Exists(fullPath))
        {
            if (!fullPath.EndsWith(".AppHost.csproj", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"'{fullPath}' is not an Aspire AppHost project.");

            string workingDirectory = FindSolutionDirectory(Path.GetDirectoryName(fullPath)!);
            return Create(fullPath, workingDirectory);
        }

        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Application path '{fullPath}' does not exist.");

        for (DirectoryInfo? directory = new(fullPath); directory is not null; directory = directory.Parent)
        {
            string sourceDirectory = Path.Combine(directory.FullName, "src");
            if (!Directory.Exists(sourceDirectory))
                continue;

            string[] candidates = Directory
                .EnumerateFiles(sourceDirectory, "*.AppHost.csproj", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .ToArray();

            if (candidates.Length > 1)
                throw new InvalidOperationException(
                    $"More than one Aspire AppHost was found under '{sourceDirectory}'. Use --root with the AppHost project path.");
            if (candidates.Length == 1)
                return Create(candidates[0], directory.FullName);
        }

        throw new InvalidOperationException(
            $"No '*.AppHost.csproj' project was found from '{fullPath}' or its parent application directories.");
    }

    private static ApplicationLocation Create(string projectPath, string workingDirectory)
    {
        string fileName = Path.GetFileNameWithoutExtension(projectPath);
        string name = fileName.EndsWith(".AppHost", StringComparison.Ordinal)
            ? fileName[..^".AppHost".Length]
            : fileName;
        return new(name, Path.GetFullPath(projectPath), workingDirectory);
    }

    private static string FindSolutionDirectory(string start)
    {
        for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
        {
            if (Directory.EnumerateFiles(directory.FullName, "*.slnx", SearchOption.TopDirectoryOnly).Any())
                return directory.FullName;
        }

        return start;
    }
}

internal interface IApplicationProcess
{
    Task<int> RunAsync(string projectPath, string workingDirectory, CancellationToken cancellationToken);
}

internal sealed class DotnetApplicationProcess : IApplicationProcess
{
    public async Task<int> RunAsync(
        string projectPath,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = CreateStartInfo(projectPath, workingDirectory);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the Aspire AppHost.");
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }

    internal static ProcessStartInfo CreateStartInfo(string projectPath, string workingDirectory)
    {
        ProcessStartInfo startInfo = new("dotnet")
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--launch-profile");
        startInfo.ArgumentList.Add("https");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(projectPath);
        return startInfo;
    }
}
