using System.Buffers;
using System.Security.Cryptography;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Storage;

/// <summary>A bounded disk spool verified before either provider can publish a visible object.</summary>
internal sealed class VerifiedObjectPayload : IAsyncDisposable
{
    private VerifiedObjectPayload(FileStream stream, long length) { Stream = stream; Length = length; }
    internal FileStream Stream { get; }
    internal long Length { get; }

    internal static async Task<VerifiedObjectPayload> CreateAsync(Stream source, long length,
        ObjectWriteIdentity identity, CancellationToken cancellationToken)
    {
        if (source is null || !source.CanRead || length <= 0 || length != identity.OriginalLength)
            throw new InvalidDataException("Object payload is invalid.");
        string path = Path.Combine(Path.GetTempPath(), "trykatch-upload-" + Guid.NewGuid().ToString("N") + ".tmp");
        FileStreamOptions options = new()
        {
            Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        FileStream spool = new(path, options);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long remaining = length;
            while (remaining > 0)
            {
                int read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), cancellationToken);
                if (read == 0) throw new InvalidDataException("Object payload is invalid.");
                hash.AppendData(buffer, 0, read);
                await spool.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                remaining -= read;
            }
            if (await source.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) != 0
                || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), identity.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Object payload is invalid.");
            await spool.FlushAsync(cancellationToken);
            spool.Position = 0;
            return new(spool, length);
        }
        catch
        {
            await spool.DisposeAsync();
            throw;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}
