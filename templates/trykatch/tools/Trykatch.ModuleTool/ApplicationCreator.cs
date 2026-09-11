using System.Diagnostics;

namespace Trykatch.ModuleTool;

internal sealed class ApplicationCreator(
    IApplicationTemplateProcess process,
    TextWriter error)
{
    public async Task<int> CreateAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (arguments.Count == 0 || string.IsNullOrWhiteSpace(arguments[0]) || arguments[0].StartsWith('-'))
        {
            await error.WriteLineAsync("error: Application name is required. Run 'trykatch new --help'.");
            return 1;
        }

        if (arguments.Skip(1).Any(IsReservedOption))
        {
            await error.WriteLineAsync(
                "error: --name and --allow-scripts are managed by 'trykatch new' and cannot be supplied explicitly.");
            return 1;
        }

        List<string> templateArguments =
        [
            "new",
            "trykatch",
            "--name",
            arguments[0],
            .. arguments.Skip(1),
            "--allow-scripts",
            "yes"
        ];

        return await process.RunAsync(templateArguments, cancellationToken);
    }

    private static bool IsReservedOption(string argument) =>
        string.Equals(argument, "--name", StringComparison.Ordinal)
        || string.Equals(argument, "-n", StringComparison.Ordinal)
        || string.Equals(argument, "--allow-scripts", StringComparison.Ordinal);
}

internal interface IApplicationTemplateProcess
{
    Task<int> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

internal sealed class DotnetApplicationTemplateProcess : IApplicationTemplateProcess
{
    public async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new("dotnet")
        {
            UseShellExecute = false
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the .NET template engine.");

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

        return process.ExitCode;
    }
}
