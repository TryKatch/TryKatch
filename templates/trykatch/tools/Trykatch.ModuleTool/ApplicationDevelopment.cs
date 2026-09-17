using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Trykatch.ModuleTool;

internal sealed record DevelopmentCheck(string Name, bool Passed, string Detail, string Remedy = "");
internal sealed record DevelopmentReport(IReadOnlyList<DevelopmentCheck> Checks)
{
    public bool IsReady => Checks.All(check => check.Passed);
}

// One seam for local onboarding. Never reads or prints environment secrets, starts
// containers, trusts certificates, or applies database migrations.
internal sealed class ApplicationDevelopment(
    IWorkspaceCommandRunner runner, IContainerRuntimeProbe runtime, HttpClient http, TextWriter output)
{
    public async Task<DevelopmentReport> DoctorAsync(string root, CancellationToken cancellationToken)
    {
        ApplicationLocation location = ApplicationLocation.Discover(root);
        List<DevelopmentCheck> checks = CheckToolchain(location, cancellationToken);
        checks.Add(CheckCommand(location.WorkingDirectory, "development HTTPS certificate", "dotnet",
            ["dev-certs", "https", "--check"], _ => true, "Run 'dotnet dev-certs https --trust' yourself.", cancellationToken));
        ContainerRuntimeStatus docker = await runtime.CheckAsync(cancellationToken);
        checks.Add(new("Docker daemon", docker.IsReady, docker.IsReady ? $"Docker {docker.ServerVersion} is ready." : docker.Message,
            "Start Docker Desktop/Engine and confirm 'docker info' succeeds."));
        ModuleDoctorReport modules = new ModuleWorkspace(location.WorkingDirectory).Inspect();
        checks.Add(new("module graph", modules.IsHealthy, modules.IsHealthy
            ? $"{modules.Modules.Count} installed modules; contracts are valid." : string.Join(Environment.NewLine, modules.Errors),
            "Run 'trykatch module doctor' for module details."));
        return new(checks);
    }

    public async Task<int> SetupAsync(string root, CancellationToken cancellationToken)
    {
        ApplicationLocation location = ApplicationLocation.Discover(root);
        DevelopmentReport tools = new(CheckToolchain(location, cancellationToken));
        Print(tools);
        if (!tools.IsReady) return 2;
        string[] solutions = Directory.GetFiles(location.WorkingDirectory, "*.slnx");
        if (solutions.Length != 1) throw new InvalidOperationException("Setup requires exactly one .slnx at the application root.");
        if (!await RestoreAsync(location.WorkingDirectory, "Restoring .NET dependencies", "dotnet",
                ["restore", solutions[0], "--locked-mode"], cancellationToken)) return 2;
        string web = Path.Combine(location.WorkingDirectory, "web");
        if (HasWeb(location) && !await RestoreAsync(web, "Installing pinned frontend dependencies", "corepack",
                ["pnpm", "install", "--frozen-lockfile"], cancellationToken)) return 2;
        await output.WriteLineAsync("Dependencies are ready. No secrets, certificates or databases were changed.");
        await output.WriteLineAsync("Next: 'trykatch doctor', then 'trykatch start'.");
        return 0;
    }

    public async Task<int> StatusAsync(string root, string? apiUrl, CancellationToken cancellationToken)
    {
        ApplicationLocation location = ApplicationLocation.Discover(root);
        await output.WriteLineAsync($"Application: {location.Name}");
        await output.WriteLineAsync($"Surface: {(HasWeb(location) ? "backend + React" : "backend only")}");
        if (apiUrl is null)
        {
            await output.WriteLineAsync("Runtime health: unknown (no API URL supplied). This is workspace configuration, not proof the app is running.");
            await output.WriteLineAsync("Use 'trykatch status --api-url <loopback API URL from Aspire>' to check live and ready health.");
            return 0;
        }
        Uri origin = ValidateLocalApiUrl(apiUrl);
        bool healthy = true;
        foreach (string endpoint in new[] { "/health/live", "/health/ready" })
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                using HttpResponseMessage response = await http.GetAsync(new Uri(origin, endpoint), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                bool passed = response.StatusCode == HttpStatusCode.OK;
                healthy &= passed;
                await output.WriteLineAsync($"{endpoint}: {(passed ? "healthy" : "unhealthy")} (HTTP {(int)response.StatusCode})");
            }
            catch (Exception exception) when (exception is HttpRequestException || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                healthy = false;
                await output.WriteLineAsync($"{endpoint}: unavailable. Confirm the Aspire API URL and trust your local HTTPS certificate.");
            }
        }
        return healthy ? 0 : 2;
    }

    internal static Uri ValidateLocalApiUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https")
            || !uri.IsLoopback || uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("--api-url must be a loopback HTTP(S) origin without credentials, path, query or fragment.", nameof(value));
        return uri;
    }

    public void Print(DevelopmentReport report)
    {
        foreach (DevelopmentCheck check in report.Checks)
        {
            output.WriteLine($"[{(check.Passed ? "OK" : "FAIL")}] {check.Name}: {check.Detail}");
            if (!check.Passed && check.Remedy.Length > 0) output.WriteLine($"  Fix: {check.Remedy}");
        }
    }

    private static bool HasWeb(ApplicationLocation location) =>
        File.Exists(Path.Combine(location.WorkingDirectory, "web", "package.json"));

    private List<DevelopmentCheck> CheckToolchain(ApplicationLocation location, CancellationToken cancellationToken)
    {
        string root = location.WorkingDirectory;
        List<DevelopmentCheck> checks =
        [
            CheckCommand(root, ".NET SDK", "dotnet", ["--version"], value => Major(value) >= 10,
                "Install the .NET 10 SDK and check the SDK selected by global.json.", cancellationToken)
        ];
        if (!HasWeb(location)) return checks;
        using JsonDocument package = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "web", "package.json")));
        string manager = package.RootElement.GetProperty("packageManager").GetString() ?? string.Empty;
        if (!manager.StartsWith("pnpm@", StringComparison.Ordinal)) throw new InvalidOperationException("web/package.json must pin pnpm via packageManager.");
        string pinned = manager["pnpm@".Length..].Split('+')[0];
        string web = Path.Combine(root, "web");
        checks.Add(CheckCommand(web, "Node.js", "node", ["--version"], value => Major(value.TrimStart('v')) >= 24,
            "Install Node.js 24 or newer.", cancellationToken));
        checks.Add(CheckCommand(web, "Corepack pnpm", "corepack", ["pnpm", "--version"], value => value == pinned,
            $"Enable Corepack and install the workspace-pinned pnpm {pinned}. Do not change the pin.", cancellationToken));
        checks.Add(CheckCommand(web, "pnpm on PATH (used by Aspire)", "pnpm", ["--version"], value => value == pinned,
            $"Run 'corepack enable pnpm'; ensure 'pnpm --version' inside web reports {pinned}.", cancellationToken));
        return checks;
    }

    private DevelopmentCheck CheckCommand(string root, string name, string executable, string[] arguments,
        Func<string, bool> accepts, string remedy, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            WorkspaceCommandResult result = runner.Run(executable, arguments, root, timeout.Token);
            string value = result.Output.Trim();
            // Only print bounded, single-line tool versions. Never relay environment dumps.
            string detail = result.ExitCode == 0 ? value.ReplaceLineEndings(" ") : "Command failed.";
            if (result.ExitCode == 0 && arguments.Contains("dev-certs")) detail = "Development HTTPS certificate exists; trust is not checked.";
            if (detail.Length == 0) detail = "Command succeeded.";
            if (detail.Length > 240) detail = detail[..237] + "...";
            return new(name, result.ExitCode == 0 && accepts(value), detail, remedy);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new(name, false, "Tool unavailable or check timed out.", remedy);
        }
    }

    private async Task<bool> RestoreAsync(string root, string label, string executable, string[] arguments, CancellationToken cancellationToken)
    {
        await output.WriteLineAsync(label + "...");
        WorkspaceCommandResult result = await Task.Run(() => runner.Run(executable, arguments, root, cancellationToken), cancellationToken);
        if (result.ExitCode == 0) return true;
        await output.WriteLineAsync($"{label} failed (exit {result.ExitCode}). Run '{executable} {string.Join(' ', arguments)}' from the application to inspect dependency errors.");
        return false;
    }

    private static int Major(string value) => int.TryParse(value.Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major) ? major : -1;
}
