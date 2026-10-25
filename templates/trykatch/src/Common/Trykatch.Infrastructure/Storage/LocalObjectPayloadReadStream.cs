namespace Trykatch.Infrastructure.Storage;

internal sealed class LocalObjectPayloadReadStream(Stream source, long length) : Stream
{
    private readonly long payloadStart = source.Position;
    private long position;

    public override bool CanRead => source.CanRead;
    public override bool CanSeek => source.CanSeek;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position
    {
        get => position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = source.Read(buffer, offset, (int)Math.Min(count, length - position));
        position += read;
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        int read = source.Read(buffer[..(int)Math.Min(buffer.Length, length - position)]);
        position += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await source.ReadAsync(buffer[..(int)Math.Min(buffer.Length, length - position)], cancellationToken);
        position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(position + offset),
            SeekOrigin.End => checked(length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (target < 0 || target > length) throw new IOException("Seek is outside the object payload.");
        source.Seek(checked(payloadStart + target), SeekOrigin.Begin);
        position = target;
        return target;
    }

    public override void Flush() => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) source.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await source.DisposeAsync();
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
