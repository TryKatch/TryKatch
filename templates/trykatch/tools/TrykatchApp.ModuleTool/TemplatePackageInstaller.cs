using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;

namespace TrykatchApp.ModuleTool;

internal sealed class TemplatePackageInstaller(
    ITemplateEngine templateEngine,
    TextWriter output,
    TextWriter error,
    bool isInteractive)
{
    private static readonly Regex SemanticVersion = new(
        "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static string CurrentVersion
    {
        get
        {
            string? informationalVersion = typeof(TemplatePackageInstaller).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            return informationalVersion?.Split('+', 2)[0]
                ?? typeof(TemplatePackageInstaller).Assembly.GetName().Version?.ToString(3)
                ?? throw new InvalidOperationException("The Trykatch CLI package version is unavailable.");
        }
    }

    public async Task<int> InstallAsync(string version, bool force, CancellationToken cancellationToken)
    {
        if (!SemanticVersion.IsMatch(version))
        {
            await error.WriteLineAsync("error: --version requires a valid semantic version, for example 0.1.0-preview.6.");
            return 1;
        }

        string package = $"Trykatch.Templates@{version}";
        string status = $"Installing Trykatch template {version}";
        Stopwatch elapsed = Stopwatch.StartNew();
        Task<TemplateEngineResult> install = templateEngine.InstallAsync(package, force, cancellationToken);

        if (isInteractive)
            await RenderProgressAsync(status, install, cancellationToken);
        else
            await output.WriteLineAsync($"{status}...");

        TemplateEngineResult result = await install;
        elapsed.Stop();

        if (result.ExitCode != 0)
        {
            if (isInteractive)
                await output.WriteLineAsync();
            await error.WriteLineAsync($"Could not install Trykatch template {version}.");
            string details = string.Join(
                Environment.NewLine,
                new[] { result.StandardOutput, result.StandardError }
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim()));
            if (details.Length > 0)
                await error.WriteLineAsync(details);
            return result.ExitCode;
        }

        if (isInteractive)
            await output.WriteAsync("\r");
        await output.WriteLineAsync($"✓ Trykatch template {version} installed ({FormatElapsed(elapsed.Elapsed)}).");
        await output.WriteLineAsync("  Next: dotnet new trykatch -n <name>");
        return 0;
    }

    private async Task RenderProgressAsync(
        string status,
        Task<TemplateEngineResult> install,
        CancellationToken cancellationToken)
    {
        string[] frames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
        int frame = 0;
        while (!install.IsCompleted)
        {
            await output.WriteAsync($"\r{frames[frame++ % frames.Length]} {status}...");
            await output.FlushAsync(cancellationToken);
            await Task.WhenAny(install, Task.Delay(TimeSpan.FromMilliseconds(80), cancellationToken));
        }
    }

    private static string FormatElapsed(TimeSpan elapsed) => elapsed.TotalSeconds < 1
        ? $"{elapsed.TotalMilliseconds:0} ms"
        : $"{elapsed.TotalSeconds:0.0} s";
}

internal sealed record TemplateEngineResult(int ExitCode, string StandardOutput, string StandardError);

internal interface ITemplateEngine
{
    Task<TemplateEngineResult> InstallAsync(
        string package,
        bool force,
        CancellationToken cancellationToken);
}

internal sealed class DotnetTemplateEngine : ITemplateEngine
{
    public async Task<TemplateEngineResult> InstallAsync(
        string package,
        bool force,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("new");
        startInfo.ArgumentList.Add("install");
        startInfo.ArgumentList.Add(package);
        if (force)
            startInfo.ArgumentList.Add("--force");

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the .NET template engine.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }

        return new(process.ExitCode, await standardOutput, await standardError);
    }
}
