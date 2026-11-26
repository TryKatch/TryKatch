using System.Buffers.Binary;
using System.Text.Json;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Storage;

internal static class LocalObjectEnvelope
{
    private static readonly byte[] Magic = "TKOBJ001"u8.ToArray();
    private const int MaxHeaderLength = 16 * 1024;

    internal static async Task WriteAsync(string path, ConditionalObjectState state, long currentLength,
        ObjectWriteIdentity identity, ObjectStorageRevision revision, Stream? payload, CancellationToken cancellationToken)
    {
        LocalObjectHeader header = new(1, state, currentLength, identity, revision.Value);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(header);
        if (json.Length is <= 0 or > MaxHeaderLength) throw new InvalidDataException("Object envelope header is invalid.");

        await using FileStream destination = new(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        await destination.WriteAsync(Magic, cancellationToken);
        byte[] size = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(size, json.Length);
        await destination.WriteAsync(size, cancellationToken);
        await destination.WriteAsync(json, cancellationToken);
        if (payload is not null) await payload.CopyToAsync(destination, cancellationToken);
        await destination.FlushAsync(cancellationToken);
    }

    internal static bool TryRead(FileStream source, out ConditionalObjectStat stat)
    {
        stat = new(ConditionalObjectState.Unowned);
        try
        {
            if (source.Length < Magic.Length + sizeof(int)) return false;
            Span<byte> magic = stackalloc byte[8];
            source.ReadExactly(magic);
            if (!magic.SequenceEqual(Magic)) return false;
            Span<byte> sizeBytes = stackalloc byte[sizeof(int)];
            source.ReadExactly(sizeBytes);
            int headerLength = BinaryPrimitives.ReadInt32LittleEndian(sizeBytes);
            if (headerLength is <= 0 or > MaxHeaderLength || source.Length < Magic.Length + sizeof(int) + headerLength)
                return false;
            byte[] json = new byte[headerLength];
            source.ReadExactly(json);
            LocalObjectHeader? header = JsonSerializer.Deserialize<LocalObjectHeader>(json);
            if (header is null || header.SchemaVersion != 1
                || header.State is not (ConditionalObjectState.Provisional or ConditionalObjectState.Cancellation)
                || !ConditionalObjectPolicy.TryIdentity(header.Identity, out ObjectWriteIdentity identity)
                || !ConditionalObjectPolicy.IsValidRevision(new ObjectStorageRevision(header.Revision))) return false;

            long payloadLength = source.Length - source.Position;
            if (header.CurrentLength != payloadLength
                || (header.State == ConditionalObjectState.Provisional && payloadLength != identity.OriginalLength)
                || (header.State == ConditionalObjectState.Cancellation && payloadLength != 0)) return false;

            stat = new(header.State, payloadLength, identity, new ObjectStorageRevision(header.Revision));
            return true;
        }
        catch (JsonException) { return false; }
        catch (EndOfStreamException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private sealed record LocalObjectHeader(int SchemaVersion, ConditionalObjectState State, long CurrentLength,
        ObjectWriteIdentity Identity, string Revision);
}
