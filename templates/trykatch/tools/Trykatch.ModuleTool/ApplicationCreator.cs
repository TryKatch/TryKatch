using System.Diagnostics;

namespace Trykatch.ModuleTool;

internal sealed class ApplicationCreator(
    IApplicationTemplateProcess process,
    Action<string>? progress = null)
{
    public async Task<ApplicationTemplateResult> CreateAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        progress?.Invoke("Validating application options");
        if (arguments.Count == 0 || string.IsNullOrWhiteSpace(arguments[0]) || arguments[0].StartsWith('-'))
        {
            return new(1, "error: Application name is required. Run 'trykatch new --help'.");
        }

        if (arguments.Skip(1).Any(IsReservedOption))
        {
            return new(1, "error: --name and --allow-scripts are managed by 'trykatch new' and cannot be supplied explicitly.");
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

        progress?.Invoke("Running application template and packaged Git setup");
        return await process.RunAsync(templateArguments, cancellationToken);
    }

    private static bool IsReservedOption(string argument) =>
        string.Equals(argument, "--name", StringComparison.Ordinal)
        || string.Equals(argument, "-n", StringComparison.Ordinal)
        || string.Equals(argument, "--allow-scripts", StringComparison.Ordinal);
}

internal interface IApplicationTemplateProcess
{
    Task<ApplicationTemplateResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

internal sealed record ApplicationTemplateResult(int ExitCode, string Output);

internal sealed class DotnetApplicationTemplateProcess : IApplicationTemplateProcess
{
    public async Task<ApplicationTemplateResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the .NET template engine.");

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> standardError = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The template engine exited between the check and termination.
            }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(standardOutput, standardError);
            throw;
        }

        await Task.WhenAll(standardOutput, standardError);
        return new(process.ExitCode, await standardOutput + await standardError);
    }
}
