using System.Text.Json;
using Trykatch.ModuleTool;

return await RunAsync(args);

static Task<int> RunAsync(string[] arguments)
{
    if (arguments.Length == 0)
        return Task.FromResult(ShowHelp());

    if (arguments.Length == 1
        && string.Equals(arguments[0], "--version", StringComparison.Ordinal))
    {
        Console.WriteLine($"Trykatch CLI {TemplatePackageInstaller.CurrentVersion}");
        return Task.FromResult(0);
    }

    if (arguments.Length == 2
        && IsHelp(arguments[0])
        && string.Equals(arguments[1], "module", StringComparison.Ordinal))
        return Task.FromResult(ShowModuleHelp());

    if (arguments.Length == 2
        && IsHelp(arguments[0])
        && string.Equals(arguments[1], "template", StringComparison.Ordinal))
        return Task.FromResult(ShowTemplateHelp());

    if (arguments.Length == 2
        && IsHelp(arguments[0])
        && string.Equals(arguments[1], "start", StringComparison.Ordinal))
        return Task.FromResult(ShowStartHelp());

    if (arguments.Length == 2
        && IsHelp(arguments[0])
        && string.Equals(arguments[1], "new", StringComparison.Ordinal))
        return Task.FromResult(ShowNewHelp());

    if (arguments.Length == 1 && IsHelp(arguments[0]))
        return Task.FromResult(ShowHelp());

    if (arguments.Length >= 2
        && string.Equals(arguments[0], "module", StringComparison.Ordinal)
        && (IsHelp(arguments[1]) || arguments.Skip(2).Any(IsHelpOption)))
        return Task.FromResult(ShowModuleHelp());

    if (string.Equals(arguments[0], "template", StringComparison.Ordinal))
        return RunTemplateAsync(arguments);

    if (string.Equals(arguments[0], "update", StringComparison.Ordinal))
        return RunTemplateAsync(["template", "update", .. arguments.Skip(1)]);

    if (string.Equals(arguments[0], "start", StringComparison.Ordinal))
        return RunStartAsync(arguments);

    if (string.Equals(arguments[0], "new", StringComparison.Ordinal))
        return RunNewAsync(arguments);

    if (arguments.Length < 2 || !string.Equals(arguments[0], "module", StringComparison.Ordinal))
        return Task.FromResult(ShowUnknownCommand(arguments[0]));

    string root = Directory.GetCurrentDirectory();
    string? expectedSha256 = null;
    string? sourceBundle = null;
    List<string> positional = [];
    for (int index = 1; index < arguments.Length; index++)
    {
        if (string.Equals(arguments[index], "--root", StringComparison.Ordinal))
        {
            if (++index >= arguments.Length)
                return Task.FromResult(Fail("--root requires a path."));
            root = arguments[index];
        }
        else if (string.Equals(arguments[index], "--sha256", StringComparison.Ordinal))
        {
            if (++index >= arguments.Length)
                return Task.FromResult(Fail("--sha256 requires a digest."));
            expectedSha256 = arguments[index];
        }
        else if (string.Equals(arguments[index], "--source-bundle", StringComparison.Ordinal))
        {
            if (++index >= arguments.Length)
                return Task.FromResult(Fail("--source-bundle requires a path."));
            sourceBundle = arguments[index];
        }
        else
        {
            positional.Add(arguments[index]);
        }
    }

    if (positional.Count == 0)
        return Task.FromResult(ShowModuleHelp(1));

    try
    {
        ModuleWorkspace workspace = new(root);
        ModuleDoctorReport report = positional[0] switch
        {
            "doctor" when positional.Count == 1 => workspace.Inspect(),
            "list" when positional.Count == 1 => workspace.Inspect(),
            "generate" when positional.Count == 1 => workspace.Generate(),
            "enable" when positional.Count == 2 => workspace.SetEnabled(positional[1], enabled: true),
            "disable" when positional.Count == 2 => workspace.SetEnabled(positional[1], enabled: false),
            "register" when positional.Count == 2 => workspace.RegisterWorkspace(positional[1]),
            "install" when positional.Count == 2 && expectedSha256 is not null =>
                workspace.InstallPackage(positional[1], expectedSha256),
            "upgrade" when positional.Count == 2 && expectedSha256 is not null =>
                workspace.UpgradePackage(positional[1], expectedSha256),
            "eject" when positional.Count == 2 && sourceBundle is not null && expectedSha256 is not null =>
                workspace.EjectPackage(positional[1], sourceBundle, expectedSha256),
            "unregister" when positional.Count == 2 => workspace.Unregister(positional[1]),
            "remove" when positional.Count == 2 => workspace.Unregister(positional[1]),
            _ => throw new ArgumentException("Unknown or incomplete module command.")
        };

        PrintModules(report.Modules);
        if (!report.IsHealthy)
        {
            foreach (string error in report.Errors)
                Console.Error.WriteLine($"error: {error}");
            return Task.FromResult(2);
        }

        Console.WriteLine(positional[0] switch
        {
            "doctor" => "Module workspace is healthy.",
            "generate" => "Module registries generated.",
            "enable" => $"Module '{positional[1]}' enabled.",
            "disable" => $"Module '{positional[1]}' disabled.",
            "register" => $"Workspace module from '{positional[1]}' registered in the disabled state.",
            "install" => $"Module package from '{positional[1]}' installed in the disabled state.",
            "upgrade" => $"Module package from '{positional[1]}' upgraded.",
            "eject" => $"Module '{positional[1]}' ejected to reviewed workspace source.",
            "unregister" or "remove" => $"Module '{positional[1]}' unregistered; its database history and data were retained.",
            _ => $"{report.Modules.Count} module(s)."
        });
        return Task.FromResult(0);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
    {
        return Task.FromResult(Fail(exception.Message));
    }
}

static async Task<int> RunNewAsync(string[] arguments)
{
    if (arguments.Length == 1 || arguments.Skip(1).Any(IsHelpOption))
        return ShowNewHelp(arguments.Length == 1 ? 1 : 0);

    try
    {
        ApplicationCreator creator = new(new DotnetApplicationTemplateProcess(), Console.Error);
        return await creator.CreateAsync(arguments.Skip(1).ToArray(), CancellationToken.None);
    }
    catch (Exception exception) when (exception is IOException
        or UnauthorizedAccessException
        or InvalidOperationException
        or System.ComponentModel.Win32Exception)
    {
        return Fail(exception.Message);
    }
}

static async Task<int> RunTemplateAsync(string[] arguments)
{
    if (arguments.Length == 1
        || IsHelp(arguments[1])
        || arguments.Skip(2).Any(IsHelpOption))
        return ShowTemplateHelp(arguments.Length == 1 ? 1 : 0);

    string operation = arguments[1];
    if (operation is not ("install" or "update" or "uninstall"))
        return ShowTemplateHelp(1);

    if (string.Equals(operation, "uninstall", StringComparison.Ordinal))
    {
        if (arguments.Length > 2)
            return Fail($"Unknown template option '{arguments[2]}'. Run 'trykatch template help'.");

        try
        {
            TemplatePackageInstaller uninstaller = new(
                new DotnetTemplateEngine(),
                Console.Out,
                Console.Error,
                !Console.IsOutputRedirected && !Console.IsErrorRedirected);
            return await uninstaller.UninstallAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return Fail(exception.Message);
        }
    }

    string version = TemplatePackageInstaller.CurrentVersion;
    bool force = string.Equals(operation, "update", StringComparison.Ordinal);
    for (int index = 2; index < arguments.Length; index++)
    {
        if (string.Equals(arguments[index], "--version", StringComparison.Ordinal))
        {
            if (++index >= arguments.Length)
                return Fail("--version requires a semantic version.");
            version = arguments[index];
        }
        else if (string.Equals(arguments[index], "--force", StringComparison.Ordinal))
        {
            force = true;
        }
        else
        {
            return Fail($"Unknown template option '{arguments[index]}'. Run 'trykatch template help'.");
        }
    }

    try
    {
        TemplatePackageInstaller installer = new(
            new DotnetTemplateEngine(),
            Console.Out,
            Console.Error,
            !Console.IsOutputRedirected && !Console.IsErrorRedirected);
        return string.Equals(operation, "update", StringComparison.Ordinal)
            ? await installer.UpdateAsync(version, CancellationToken.None)
            : await installer.InstallAsync(version, force, CancellationToken.None);
    }
    catch (Exception exception) when (exception is IOException
        or UnauthorizedAccessException
        or InvalidOperationException
        or System.ComponentModel.Win32Exception)
    {
        return Fail(exception.Message);
    }
}

static async Task<int> RunStartAsync(string[] arguments)
{
    if (arguments.Skip(1).Any(IsHelpOption))
        return ShowStartHelp();

    string root = Directory.GetCurrentDirectory();
    for (int index = 1; index < arguments.Length; index++)
    {
        if (!string.Equals(arguments[index], "--root", StringComparison.Ordinal))
            return Fail($"Unknown start option '{arguments[index]}'. Run 'trykatch start --help'.");
        if (++index >= arguments.Length)
            return Fail("--root requires a path.");
        root = arguments[index];
    }

    try
    {
        ApplicationStarter starter = new(
            new DotnetApplicationProcess(),
            new DockerContainerRuntimeProbe(),
            Console.Out);
        return await starter.StartAsync(root, CancellationToken.None);
    }
    catch (Exception exception) when (exception is IOException
        or UnauthorizedAccessException
        or InvalidOperationException
        or ArgumentException
        or System.ComponentModel.Win32Exception)
    {
        return Fail(exception.Message);
    }
}

static void PrintModules(IEnumerable<ModuleStatus> modules)
{
    foreach (ModuleStatus module in modules.OrderBy(module => module.Id, StringComparer.Ordinal))
        Console.WriteLine($"{module.Id,-24} {module.Version,-12} {(module.Enabled ? "enabled" : "disabled"),-9} {module.Name}");
}

static bool IsHelp(string argument) =>
    string.Equals(argument, "help", StringComparison.Ordinal)
    || IsHelpOption(argument);

static bool IsHelpOption(string argument) =>
    string.Equals(argument, "--help", StringComparison.Ordinal)
    || string.Equals(argument, "-h", StringComparison.Ordinal);

static int ShowHelp()
{
    Console.WriteLine("Trykatch application and module toolkit");
    Console.WriteLine();
    Console.WriteLine("Create an application:");
    Console.WriteLine("  trykatch template install");
    Console.WriteLine("  trykatch update                 Update the template to this CLI's version.");
    Console.WriteLine("  trykatch template uninstall    Remove the installed project template.");
    Console.WriteLine("  trykatch new <name> [options]  Create an application and initialize Git.");
    Console.WriteLine("  trykatch start                  Start a generated application through Aspire.");
    Console.WriteLine();
    Console.WriteLine("Application options:");
    Console.WriteLine("  --ui <react|none>  Include the React frontend or generate a backend-only application.");
    Console.WriteLine("  --email            Include SMTP email and local Mailpit support.");
    Console.WriteLine("  --storage          Include local and S3-compatible object storage.");
    Console.WriteLine("  --documents        Include spreadsheet and PDF exporters.");
    Console.WriteLine("  --images           Include image validation and processing.");
    Console.WriteLine();
    Console.WriteLine("Module lifecycle:");
    Console.WriteLine("  trykatch module help     Show every module command and option.");
    Console.WriteLine("  trykatch module list     List installed modules and their state.");
    Console.WriteLine("  trykatch module doctor   Validate the full-stack module graph.");
    Console.WriteLine();
    Console.WriteLine("Run 'trykatch new --help' for application-generation options.");
    return 0;
}

static int ShowNewHelp(int exitCode = 0)
{
    Console.WriteLine("Create a Trykatch application");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  trykatch new <name> [options]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --output <path>     Write the application to a specific directory.");
    Console.WriteLine("  --ui <react|none>   Include the React frontend or generate a backend-only application.");
    Console.WriteLine("  --email             Include SMTP email and local Mailpit support.");
    Console.WriteLine("  --storage           Include local and S3-compatible object storage.");
    Console.WriteLine("  --documents         Include spreadsheet and PDF exporters.");
    Console.WriteLine("  --images            Include image validation and processing.");
    Console.WriteLine();
    Console.WriteLine("The generated application is initialized as a Git repository on the main branch.");
    Console.WriteLine("When the output is already inside a Git worktree, the parent repository is preserved.");
    return exitCode;
}

static int ShowTemplateHelp(int exitCode = 0)
{
    Console.WriteLine("Trykatch template installation");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  trykatch template install [--version <version>] [--force]");
    Console.WriteLine("  trykatch template update [--version <version>]");
    Console.WriteLine("  trykatch template uninstall");
    Console.WriteLine("  trykatch update [--version <version>]  Alias for 'template update'.");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --version <version>  Select a specific Trykatch.Templates version.");
    Console.WriteLine("  --force              Reinstall when the selected version is already present.");
    Console.WriteLine();
    Console.WriteLine("These commands use the official .NET template engine and show progress.");
    return exitCode;
}

static int ShowStartHelp(int exitCode = 0)
{
    Console.WriteLine("Start a generated Trykatch application");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  trykatch start [--root <path>]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --root <path>  Application directory or AppHost project. Defaults to the current directory.");
    Console.WriteLine();
    Console.WriteLine("The command verifies Docker, discovers the Aspire AppHost, and runs its HTTPS launch profile.");
    Console.WriteLine("Docker CLI 25.0 or newer and a running daemon are required. Press Ctrl+C to stop the application.");
    return exitCode;
}

static int ShowModuleHelp(int exitCode = 0)
{
    Console.WriteLine("Trykatch module lifecycle");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  trykatch module list [--root <path>]");
    Console.WriteLine("  trykatch module doctor [--root <path>]");
    Console.WriteLine("  trykatch module generate [--root <path>]");
    Console.WriteLine("  trykatch module enable <id> [--root <path>]");
    Console.WriteLine("  trykatch module disable <id> [--root <path>]");
    Console.WriteLine("  trykatch module register <manifest> [--root <path>]");
    Console.WriteLine("  trykatch module install <manifest> --sha256 <digest> [--root <path>]");
    Console.WriteLine("  trykatch module upgrade <manifest> --sha256 <digest> [--root <path>]");
    Console.WriteLine("  trykatch module eject <id> --source-bundle <path> --sha256 <digest> [--root <path>]");
    Console.WriteLine("  trykatch module unregister <id> [--root <path>]");
    Console.WriteLine("  trykatch module remove <id> [--root <path>]  Alias for unregister.");
    return exitCode;
}

static int ShowUnknownCommand(string command)
{
    Console.Error.WriteLine($"error: Unknown command '{command}'. Run 'trykatch help' for available commands.");
    return 1;
}

static int Fail(string message)
{
    Console.Error.WriteLine($"error: {message}");
    return 1;
}
