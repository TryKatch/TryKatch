using System.Diagnostics;

namespace Trykatch.IntegrationTests;

internal static class ProcessExecution
{
    public static async Task<(int Exit, string Output, string Error)> WaitAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken);
        }
        catch (Exception failure) when (failure is TimeoutException or OperationCanceledException)
        {
            // Cleanup belongs to this exact started process, not to its executable name.
            // The cancelled caller must not cancel termination or leave pipe reads alive.
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited) { }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            throw;
        }
        return (process.ExitCode, await stdout, await stderr);
    }
}
