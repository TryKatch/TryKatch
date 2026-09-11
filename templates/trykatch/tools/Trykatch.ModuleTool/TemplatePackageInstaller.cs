using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Trykatch.ModuleTool;

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
        return await ApplyAsync(
            version,
            force,
            $"Installing Trykatch template {version}",
            $"Trykatch template {version} installed",
            $"Could not install Trykatch template {version}.",
            cancellationToken);
    }

    public async Task<int> UpdateAsync(string version, CancellationToken cancellationToken)
    {
        return await ApplyAsync(
            version,
            force: true,
            $"Updating Trykatch template to {version}",
            $"Trykatch template updated to {version}",
            $"Could not update Trykatch template to {version}.",
            cancellationToken);
    }

    public async Task<int> UninstallAsync(CancellationToken cancellationToken)
    {
        const string packageId = "Trykatch.Templates";
        Stopwatch elapsed = Stopwatch.StartNew();
        Task<TemplateEngineResult> uninstall = templateEngine.UninstallAsync(packageId, cancellationToken);

        if (isInteractive)
            await RenderProgressAsync("Uninstalling Trykatch template", uninstall, cancellationToken);
        else
            await output.WriteLineAsync("Uninstalling Trykatch template...");

        TemplateEngineResult result = await uninstall;
        elapsed.Stop();

        if (result.ExitCode != 0)
        {
            if (isInteractive)
                await output.WriteLineAsync();
            await error.WriteLineAsync("Could not uninstall the Trykatch template.");
            await WriteFailureDetailsAsync(result);
            return result.ExitCode;
        }

        if (isInteractive)
            await output.WriteAsync("\r");
        await output.WriteLineAsync($"✓ Trykatch template uninstalled ({FormatElapsed(elapsed.Elapsed)}).");
        await output.WriteLineAsync("  To remove the CLI too: dotnet tool uninstall --global Trykatch.Cli");
        return 0;
    }

    private async Task<int> ApplyAsync(
        string version,
        bool force,
        string status,
        string completion,
        string failure,
        CancellationToken cancellationToken)
    {
        if (!SemanticVersion.IsMatch(version))
        {
            await error.WriteLineAsync("error: --version requires a valid semantic version, for example 0.1.0-preview.12.");
            return 1;
        }

        const string packageId = "Trykatch.Templates";
        if (!force && await templateEngine.IsPackageInstalledAsync(packageId, version, cancellationToken))
        {
            await output.WriteLineAsync($"✓ Trykatch template {version} is already installed; no update is required.");
            await output.WriteLineAsync("  Next: trykatch new <name>");
            return 0;
        }

        string package = $"{packageId}@{version}";
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
            await error.WriteLineAsync(failure);
            await WriteFailureDetailsAsync(result);
            return result.ExitCode;
        }

        if (isInteractive)
            await output.WriteAsync("\r");
        await output.WriteLineAsync($"✓ {completion} ({FormatElapsed(elapsed.Elapsed)}).");
        await output.WriteLineAsync("  Next: trykatch new <name>");
        return 0;
    }

    private async Task WriteFailureDetailsAsync(TemplateEngineResult result)
    {
        string details = string.Join(
            Environment.NewLine,
            new[] { result.StandardOutput, result.StandardError }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim()));
        if (details.Length > 0)
            await error.WriteLineAsync(details);
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
    Task<bool> IsPackageInstalledAsync(
        string packageId,
        string version,
        CancellationToken cancellationToken);

    Task<TemplateEngineResult> InstallAsync(
        string package,
        bool force,
        CancellationToken cancellationToken);

    Task<TemplateEngineResult> UninstallAsync(
        string packageId,
        CancellationToken cancellationToken);
}

internal sealed class DotnetTemplateEngine(string? templateEngineHome = null) : ITemplateEngine
{
    private const string PackagesFileName = "packages.json";

    public async Task<bool> IsPackageInstalledAsync(
        string packageId,
        string version,
        CancellationToken cancellationToken)
    {
        string userProfileVariable = OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME";
        string userProfile = Environment.GetEnvironmentVariable(userProfileVariable)
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string home = templateEngineHome ?? Path.Combine(userProfile, ".templateengine");
        string packagesFile = Path.Combine(home, PackagesFileName);
        if (!File.Exists(packagesFile))
            return false;

        try
        {
            await using FileStream stream = File.Open(
                packagesFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream, detectEncodingFromByteOrderMarks: true);
            string json = await reader.ReadToEndAsync(cancellationToken);
            using JsonDocument document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("Packages", out JsonElement packages)
                || packages.ValueKind is not JsonValueKind.Array)
            {
                return false;
            }

            foreach (JsonElement package in packages.EnumerateArray())
            {
                if (!package.TryGetProperty("Details", out JsonElement details)
                    || !details.TryGetProperty("PackageId", out JsonElement installedId)
                    || !details.TryGetProperty("Version", out JsonElement installedVersion))
                {
                    continue;
                }

                if (string.Equals(installedId.GetString(), packageId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(installedVersion.GetString(), version, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (IOException)
        {
            // The template engine may be updating its registry concurrently. Let the
            // install command provide the authoritative result in that rare case.
        }
        catch (JsonException)
        {
            // A corrupt or incompatible registry must not be mistaken for an install.
        }

        return false;
    }

    public async Task<TemplateEngineResult> InstallAsync(
        string package,
        bool force,
        CancellationToken cancellationToken)
    {
        List<string> arguments = ["new", "install", package];
        if (force)
            arguments.Add("--force");
        return await RunAsync(arguments, cancellationToken);
    }

    public Task<TemplateEngineResult> UninstallAsync(
        string packageId,
        CancellationToken cancellationToken) =>
        RunAsync(["new", "uninstall", packageId], cancellationToken);

    private static async Task<TemplateEngineResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

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
