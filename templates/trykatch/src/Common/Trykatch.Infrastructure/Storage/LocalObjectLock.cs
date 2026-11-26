using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Trykatch.Infrastructure.Storage;

internal static class LocalObjectLock
{
    internal static async Task<FileStream> AcquireAsync(string root, string canonicalKey,
        CancellationToken cancellationToken)
    {
        string name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalKey))) + ".lock";
        string directory = Path.Combine(root, "_module-uploads", "v1", ".locks");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        Stopwatch timer = Stopwatch.StartNew();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                FileStream held = new(path, new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous
                });
                try
                {
                    using FileStream probe = new(path, new FileStreamOptions
                    {
                        Mode = FileMode.OpenOrCreate,
                        Access = FileAccess.ReadWrite,
                        Share = FileShare.None,
                        Options = FileOptions.Asynchronous
                    });
                    held.Dispose();
                    throw new IOException("The filesystem does not enforce exclusive object locks.");
                }
                catch (IOException exception) when (IsKnownContention(exception))
                {
                    return held;
                }
                catch
                {
                    held.Dispose();
                    throw;
                }
            }
            catch (IOException exception) when (IsKnownContention(exception))
            {
                TimeSpan remaining = ConditionalObjectPolicy.LockTimeout - timer.Elapsed;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("Timed out acquiring the object lock.", exception);
                await Task.Delay(remaining < TimeSpan.FromMilliseconds(20)
                    ? remaining : TimeSpan.FromMilliseconds(20), cancellationToken);
            }
        }
    }

    private static bool IsKnownContention(IOException exception)
    {
        if (OperatingSystem.IsLinux()) return exception.HResult == 11;
        if (OperatingSystem.IsMacOS()) return exception.HResult == 35;
        if (!OperatingSystem.IsWindows()) return false;
        return exception.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021);
    }
}
