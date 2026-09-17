using System.Text.Json;
using Trykatch.ModuleTool;

using CancellationTokenSource shutdown = new();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};
Console.CancelKeyPress += cancelHandler;
try
{
    return await RunAsync(args, shutdown.Token);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    Console.Error.WriteLine(args.FirstOrDefault() == "new"
        ? "Application creation canceled. The template engine may have left partial output; review it before retrying."
        : args.FirstOrDefault() == "setup"
        ? "Setup canceled. Dependency installation may be partial; rerun setup to finish. No database migrations were run."
        : args.FirstOrDefault() is "doctor" or "status"
        ? "Development check canceled."
        : "Operation canceled. Any in-progress workspace transaction was rolled back.");
    return 130;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}

static Task<int> RunAsync(string[] arguments, CancellationToken cancellationToken)
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

    if (arguments.Length == 2 && IsHelp(arguments[0]) && arguments[1] is "doctor" or "setup" or "status")
        return RunDevelopmentAsync([arguments[1], "--help"], cancellationToken);

    if (arguments.Length == 1 && IsHelp(arguments[0]))
        return Task.FromResult(ShowHelp());

    if (arguments.Length >= 2
        && string.Equals(arguments[0], "module", StringComparison.Ordinal)
        && (IsHelp(arguments[1]) || arguments.Skip(2).Any(IsHelpOption)))
        return Task.FromResult(string.Equals(arguments[1], "create", StringComparison.Ordinal)
            ? ShowModuleCreateHelp()
            : ShowModuleHelp());

    if (string.Equals(arguments[0], "template", StringComparison.Ordinal))
        return RunTemplateAsync(arguments, cancellationToken);

    if (string.Equals(arguments[0], "update", StringComparison.Ordinal))
        return RunTemplateAsync(["template", "update", .. arguments.Skip(1)], cancellationToken);

    if (string.Equals(arguments[0], "start", StringComparison.Ordinal))
        return RunStartAsync(arguments, cancellationToken);

    if (arguments[0] is "doctor" or "setup" or "status")
        return RunDevelopmentAsync(arguments, cancellationToken);

    if (string.Equals(arguments[0], "new", StringComparison.Ordinal))
        return RunNewAsync(arguments, cancellationToken);

    if (arguments.Length < 2 || !string.Equals(arguments[0], "module", StringComparison.Ordinal))
        return Task.FromResult(ShowUnknownCommand(arguments[0]));

    string root = Directory.GetCurrentDirectory();
    string? expectedSha256 = null;
    string? sourceBundle = null;
    string? entity = null;
    string? resource = null;
    string? ownership = null;
    string? description = null;
    string? fieldSpecification = null;
    string? blueprintPath = null;
    bool includeWeb = false;
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
        else if (string.Equals(arguments[index], "--entity", StringComparison.Ordinal))
        {
            if (++index >= arguments.Length)
                return Task.FromResult(Fail("--entity requires a PascalCase entity name."));
            entity = arguments[index];
        }
        else if (string.Equals(arguments[index], "--resource", StringComparison.Ordinal))
        {
            if (++index >= arguments.Length)
                return Task.FromResult(Fail("--resource requires a snake_case resource name."));
            resource = arguments[index];
        }
        else if (string.Equals(arguments[index], "--ownership", StringComparison.Ordinal))
        {
            if (++index >= arguments.Length)
                return Task.FromResult(Fail("--ownership requires 'organization'."));
            ownership = arguments[index];
        }
        else if (string.Equals(arguments[index], "--description", StringComparison.Ordinal))
        {
            if (++index >= arguments.Length)
                return Task.FromResult(Fail("--description requires text."));
            description = arguments[index];
        }
        else if (string.Equals(arguments[index], "--fields", StringComparison.Ordinal))
        {
            if (++index >= arguments.Length)
                return Task.FromResult(Fail("--fields requires a field contract."));
            fieldSpecification = arguments[index];
        }
        else if (string.Equals(arguments[index], "--blueprint", StringComparison.Ordinal))
        {
            if (++index >= arguments.Length || blueprintPath is not null)
                return Task.FromResult(Fail("--blueprint requires one JSON file path."));
            blueprintPath = Path.GetFullPath(arguments[index]);
        }
        else if (string.Equals(arguments[index], "--with-web", StringComparison.Ordinal))
        {
            includeWeb = true;
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
        if (blueprintPath is not null)
        {
            if (entity is not null || resource is not null || ownership is not null || fieldSpecification is not null || description is not null)
                return Task.FromResult(Fail("--blueprint cannot be combined with --entity, --resource, --ownership, --fields or --description."));
            if (positional[0] is not "create" and not "validate")
                return Task.FromResult(Fail("--blueprint is supported by module create and module validate."));
            ModuleBlueprint blueprint = ModuleBlueprint.Load(blueprintPath);
            entity = blueprint.Entity;
            resource = blueprint.Resource;
            ownership = blueprint.Ownership;
            description = blueprint.Labels.Plural.En;
            if (positional[0] == "validate")
            {
                if (positional.Count != 1) return Task.FromResult(Fail("Usage: trykatch module validate --blueprint <file> [--root <path>] [--with-web]"));
                new ModuleScaffolder(root).Validate(new(blueprint.Module, entity, resource, ownership, description, includeWeb, BlueprintPath: blueprintPath));
                Console.WriteLine($"Blueprint '{blueprint.Module}' and workspace are valid. No files were changed.");
                return Task.FromResult(0);
            }
        }
        if (string.Equals(positional[0], "create", StringComparison.Ordinal))
        {
            if (positional.Count != 2 || entity is null || resource is null || ownership is null)
                return Task.FromResult(ShowModuleCreateHelp(1));
            using CliOperationProgress progress = new(Console.Out, IsInteractiveProgress());
            ModuleCreationResult created;
            try
            {
                created = new ModuleScaffolder(root, new ProcessWorkspaceCommandRunner(), progress: progress.Report).Create(new(
                    positional[1], entity, resource, ownership, description, includeWeb, fieldSpecification, blueprintPath),
                    cancellationToken);
                progress.Complete();
            }
            catch
            {
                progress.Fail();
                throw;
            }
            PrintModules(created.Report.Modules);
            Console.WriteLine();
            Console.WriteLine($"Module '{created.ModuleId}' created, registered, and enabled.");
            foreach (string path in created.CreatedPaths)
                Console.WriteLine($"  created: {path}");
            Console.WriteLine("  endpoints:");
            foreach (string endpoint in created.Endpoints)
                Console.WriteLine($"    {endpoint}");
            Console.WriteLine("  permissions:");
            foreach (string permission in created.Permissions)
                Console.WriteLine($"    {permission}");
            if (created.IncludeWeb) Console.WriteLine($"  Web: /{resource}");
            Console.WriteLine($"Run '{created.StartCommand}' to apply migrations and start the application.");
            return Task.FromResult(0);
        }

        ModuleWorkspace workspace = new(root);
        if (positional[0] == "facts" && positional.Count is 1 or 2)
        {
            Console.WriteLine(workspace.Facts(positional.Count == 2 ? positional[1] : null).GetRawText());
            return Task.FromResult(0);
        }
        using CliOperationProgress? workspaceProgress = positional[0] is "generate" or "register" or "install" or "upgrade" or "eject"
            ? new(Console.Out, IsInteractiveProgress(), "Module workspace operation")
            : null;
        workspaceProgress?.ReportStep(positional[0] switch
        {
            "generate" => "Generating module registries and lock file",
            "register" => "Registering module and restoring dependencies",
            "install" => "Verifying and installing module packages",
            "upgrade" => "Verifying and upgrading module packages",
            _ => "Exporting module source and restoring dependencies"
        });
        ModuleDoctorReport report;
        try
        {
            report = positional[0] switch
            {
                "doctor" when positional.Count == 1 => workspace.Inspect(),
                "list" when positional.Count == 1 => workspace.Inspect(),
                "generate" when positional.Count == 1 => workspace.Generate(cancellationToken),
                "enable" when positional.Count == 2 => workspace.SetEnabled(positional[1], enabled: true, cancellationToken),
                "disable" when positional.Count == 2 => workspace.SetEnabled(positional[1], enabled: false, cancellationToken),
                "register" when positional.Count == 2 => workspace.RegisterWorkspace(positional[1], cancellationToken),
                "install" when positional.Count == 2 && expectedSha256 is not null =>
                    workspace.InstallPackage(positional[1], expectedSha256, cancellationToken),
                "upgrade" when positional.Count == 2 && expectedSha256 is not null =>
                    workspace.UpgradePackage(positional[1], expectedSha256, cancellationToken),
                "eject" when positional.Count == 2 && sourceBundle is not null && expectedSha256 is not null =>
                    workspace.EjectPackage(positional[1], sourceBundle, expectedSha256, cancellationToken),
                "unregister" when positional.Count == 2 => workspace.Unregister(positional[1], cancellationToken),
                "remove" when positional.Count == 2 => workspace.Unregister(positional[1], cancellationToken),
                _ => throw new ArgumentException("Unknown or incomplete module command.")
            };
            if (report.IsHealthy) workspaceProgress?.Complete();
            else workspaceProgress?.Fail();
        }
        catch
        {
            workspaceProgress?.Fail();
            throw;
        }

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

static async Task<int> RunNewAsync(string[] arguments, CancellationToken cancellationToken)
{
    if (arguments.Length == 1 || arguments.Skip(1).Any(IsHelpOption))
        return ShowNewHelp(arguments.Length == 1 ? 1 : 0);

    using CliOperationProgress progress = new(Console.Out, IsInteractiveProgress(), "Application generation");
    try
    {
        ApplicationCreator creator = new(new DotnetApplicationTemplateProcess(), progress.ReportStep);
        bool verbose = arguments.Skip(1).Contains("--verbose", StringComparer.Ordinal);
        ApplicationTemplateResult result = await creator.CreateAsync(
            arguments.Skip(1).Where(argument => argument != "--verbose").ToArray(), cancellationToken);
        if (result.ExitCode == 0) progress.Complete();
        else progress.Fail();
        string displayOutput = ApplicationCreationOutput.Format(result, verbose);
        if (!string.IsNullOrWhiteSpace(displayOutput))
            await (result.ExitCode == 0 ? Console.Out : Console.Error).WriteLineAsync(displayOutput);
        return result.ExitCode;
    }
    catch (Exception exception) when (exception is IOException
        or UnauthorizedAccessException
        or InvalidOperationException
        or System.ComponentModel.Win32Exception)
    {
        progress.Fail();
        return Fail(exception.Message);
    }
    catch (OperationCanceledException)
    {
        progress.Fail();
        throw;
    }
}

static bool IsInteractiveProgress() => !Console.IsOutputRedirected
    && !string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.Ordinal);

static async Task<int> RunTemplateAsync(string[] arguments, CancellationToken cancellationToken)
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
                IsInteractiveProgress() && !Console.IsErrorRedirected);
            return await uninstaller.UninstallAsync(cancellationToken);
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
    bool force = false;
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
            IsInteractiveProgress() && !Console.IsErrorRedirected);
        return string.Equals(operation, "update", StringComparison.Ordinal)
            ? await installer.UpdateAsync(version, force, cancellationToken)
            : await installer.InstallAsync(version, force, cancellationToken);
    }
    catch (Exception exception) when (exception is IOException
        or UnauthorizedAccessException
        or InvalidOperationException
        or System.ComponentModel.Win32Exception)
    {
        return Fail(exception.Message);
    }
}

static async Task<int> RunStartAsync(string[] arguments, CancellationToken cancellationToken)
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
        return await starter.StartAsync(root, cancellationToken);
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

static async Task<int> RunDevelopmentAsync(string[] arguments, CancellationToken cancellationToken)
{
    if (arguments.Skip(1).Any(IsHelpOption))
    {
        Console.WriteLine("trykatch doctor [--root <application>]  Check toolchain, Docker, HTTPS certificate and module graph.");
        Console.WriteLine("trykatch setup [--root <application>]   Restore locked .NET and pinned frontend dependencies.");
        Console.WriteLine("trykatch status [--root <application>] [--api-url <loopback origin>]  Show configuration or check API health.");
        Console.WriteLine("Doctor does not repair configuration. Setup does not change secrets, trust certificates or migrate databases.");
        return 0;
    }
    string root = Directory.GetCurrentDirectory();
    string? apiUrl = null;
    for (int index = 1; index < arguments.Length; index++)
    {
        string option = arguments[index];
        if (option != "--root" && !(arguments[0] == "status" && option == "--api-url"))
            return Fail($"Unknown {arguments[0]} option '{option}'.");
        if (++index >= arguments.Length) return Fail($"{option} requires a value.");
        if (option == "--root") root = arguments[index]; else apiUrl = arguments[index];
    }
    try
    {
        using HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false });
        ApplicationDevelopment development = new(new ProcessWorkspaceCommandRunner(), new DockerContainerRuntimeProbe(), http, Console.Out);
        if (arguments[0] == "setup") return await development.SetupAsync(root, cancellationToken);
        if (arguments[0] == "status") return await development.StatusAsync(root, apiUrl, cancellationToken);
        DevelopmentReport report = await development.DoctorAsync(root, cancellationToken);
        development.Print(report);
        return report.IsReady ? 0 : 2;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
        or ArgumentException or JsonException or System.ComponentModel.Win32Exception)
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
    Console.WriteLine("  trykatch setup                  Restore local dependencies without changing databases or secrets.");
    Console.WriteLine("  trykatch doctor                 Check the application's local development prerequisites.");
    Console.WriteLine("  trykatch status                 Show configuration; use --api-url to probe live API health.");
    Console.WriteLine();
    Console.WriteLine("Application options:");
    Console.WriteLine("  --ui <react|none>  Include the React frontend or generate a backend-only application.");
    Console.WriteLine("  --display-name <name>  Customer-facing brand, independent of the project namespace.");
    Console.WriteLine("  --email            Include SMTP email and local Mailpit support.");
    Console.WriteLine("  --storage          Include local and S3-compatible object storage.");
    Console.WriteLine("  --documents        Include spreadsheet and PDF exporters.");
    Console.WriteLine("  --images           Include image validation and processing.");
    Console.WriteLine();
    Console.WriteLine("Module lifecycle:");
    Console.WriteLine("  trykatch module help     Show every module command and option.");
    Console.WriteLine("  trykatch module list     List installed modules and their state.");
    Console.WriteLine("  trykatch module doctor   Validate the full-stack module graph.");
    Console.WriteLine("  trykatch module facts    Print validated installed module facts as JSON.");
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
    Console.WriteLine("  --display-name <name>  Customer-facing brand, independent of the project namespace.");
    Console.WriteLine("  --email             Include SMTP email and local Mailpit support.");
    Console.WriteLine("  --storage           Include local and S3-compatible object storage.");
    Console.WriteLine("  --documents         Include spreadsheet and PDF exporters.");
    Console.WriteLine("  --images            Include image validation and processing.");
    Console.WriteLine("  --verbose           Show full template engine diagnostics for file conflicts.");
    Console.WriteLine();
    Console.WriteLine("The generated application is initialized as a Git repository on the main branch.");
    Console.WriteLine("When the output is already inside a Git worktree, the parent repository is preserved.");
    Console.WriteLine("Live progress shows validation and template/Git setup; engine diagnostics follow completion.");
    return exitCode;
}

static int ShowTemplateHelp(int exitCode = 0)
{
    Console.WriteLine("Trykatch template installation");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  trykatch template install [--version <version>] [--force]");
    Console.WriteLine("  trykatch template update [--version <version>] [--force]");
    Console.WriteLine("  trykatch template uninstall");
    Console.WriteLine("  trykatch update [--version <version>] [--force]  Alias for 'template update'.");
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
    Console.WriteLine("  trykatch module facts [module-id] [--root <path>]");
    Console.WriteLine("  trykatch module validate --blueprint <file> [--root <path>] [--with-web]");
    Console.WriteLine("  trykatch module create <name> --blueprint <file> [--with-web] [--root <path>]");
    Console.WriteLine("  trykatch module generate [--root <path>]");
    Console.WriteLine("  trykatch module create <name> --entity <name> --resource <name> --ownership organization [--fields <contract>] [--with-web] [--root <path>]");
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

static int ShowModuleCreateHelp(int exitCode = 0)
{
    Console.WriteLine("Create an organization-owned Trykatch module");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  trykatch module create <ModuleName> --entity <EntityName> --resource <snake_case_name> --ownership organization [options]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --entity <name>       PascalCase domain entity name, for example Invoice.");
    Console.WriteLine("  --resource <name>     Explicit snake_case API resource and PostgreSQL table name.");
    Console.WriteLine("  --ownership <value>   Must be organization in version 1.");
    Console.WriteLine("  --description <text>  Module description; defaults to an organization-owned description.");
    Console.WriteLine("  --fields <contract>   Comma-separated business fields; defaults to required Name and optional Description.");
    Console.WriteLine("                        Example: number:string:required:max(40),total:decimal:required,status:enum(Draft,Paid)");
    Console.WriteLine("                        Types: string, decimal, int, long, bool, date, datetime, guid, enum(...).");
    Console.WriteLine("  --with-web            Also generate and verify a React module contribution.");
    Console.WriteLine("  --blueprint <file>    Versioned JSON fields, rules, workflow actions and EN/FR labels.");
    Console.WriteLine("                        Replaces --entity, --resource, --ownership, --fields and --description.");
    Console.WriteLine("                        Example: trykatch module create ShipmentReceptions --blueprint blueprints/shipment-reception.json --with-web");
    Console.WriteLine("  --root <path>         Generated application root; defaults to the current directory.");
    Console.WriteLine();
    Console.WriteLine("The operation is atomic: source, registration, restore, and verification roll back together on failure.");
    Console.WriteLine("Live progress shows the current step and elapsed time; redirected output uses plain log lines.");
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
