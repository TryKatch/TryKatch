using TrykatchApp.ModuleTool;
using System.Text.Json;

return await RunAsync(args);

static Task<int> RunAsync(string[] arguments)
{
    if (arguments.Length == 1
        && (string.Equals(arguments[0], "--help", StringComparison.Ordinal)
            || string.Equals(arguments[0], "-h", StringComparison.Ordinal)))
        return Task.FromResult(ShowUsage(0));

    if (arguments.Length < 2 || !string.Equals(arguments[0], "module", StringComparison.Ordinal))
        return Task.FromResult(ShowUsage(1));

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
        return Task.FromResult(ShowUsage(1));

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

static void PrintModules(IEnumerable<ModuleStatus> modules)
{
    foreach (ModuleStatus module in modules.OrderBy(module => module.Id, StringComparer.Ordinal))
        Console.WriteLine($"{module.Id,-24} {module.Version,-12} {(module.Enabled ? "enabled" : "disabled"),-9} {module.Name}");
}

static int ShowUsage(int exitCode)
{
    Console.WriteLine("Trykatch module lifecycle tool");
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
    return exitCode;
}

static int Fail(string message)
{
    Console.Error.WriteLine($"error: {message}");
    return 1;
}
