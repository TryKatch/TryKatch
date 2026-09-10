using System.Diagnostics;

namespace TrykatchApp.ModuleTool;

internal sealed class ApplicationStarter(
    IApplicationProcess applicationProcess,
    TextWriter output)
{
    public async Task<int> StartAsync(string root, CancellationToken cancellationToken)
    {
        ApplicationLocation location = ApplicationLocation.Discover(root);
        await output.WriteLineAsync($"Starting {location.Name} through its Aspire AppHost...");
        await output.WriteLineAsync("Docker must remain available while the application is running.");

        return await applicationProcess.RunAsync(
            location.ProjectPath,
            location.WorkingDirectory,
            cancellationToken);
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
