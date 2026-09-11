using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using System.IO.Pipelines;

namespace Trykatch.Modules.AspNetCore;

/// <summary>
/// Stages a bounded mutation response until the owning application transaction
/// has committed or rolled back. This never invokes the downstream pipeline twice.
/// </summary>
public static class AtomicMutationResponse
{
    public static async Task ExecuteAsync(
        HttpContext context,
        int maximumBytes,
        RequestDelegate next,
        Func<CancellationToken, Task> finalizeTransaction)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(finalizeTransaction);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);

        IHttpResponseBodyFeature clientBody = context.Features.Get<IHttpResponseBodyFeature>()
            ?? throw new InvalidOperationException("The HTTP response body feature is unavailable.");
        await using StagedResponseBodyFeature staged = new(maximumBytes);
        context.Features.Set<IHttpResponseBodyFeature>(staged);
        try
        {
            await next(context);
            await staged.FlushAsync(context.RequestAborted);
            await finalizeTransaction(context.RequestAborted);
            context.Features.Set(clientBody);
            await staged.CopyToAsync(clientBody.Stream, context.RequestAborted);
            if (staged.CompleteRequested) await clientBody.CompleteAsync();
        }
        finally
        {
            context.Features.Set(clientBody);
        }
    }

    private sealed class StagedResponseBodyFeature(int maximumBytes) : IHttpResponseBodyFeature, IAsyncDisposable
    {
        private readonly BoundedResponseStream stream = new(maximumBytes);
        private PipeWriter? writer;

        public Stream Stream => stream;
        public PipeWriter Writer => writer ??= PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
        public bool CompleteRequested { get; private set; }

        public void DisableBuffering() { }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            await using FileStream file = File.OpenRead(path);
            file.Position = offset;
            long remaining = count ?? file.Length - offset;
            byte[] buffer = new byte[Math.Min(81920, maximumBytes)];
            while (remaining > 0)
            {
                int read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                if (read == 0) break;
                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                remaining -= read;
            }
        }

        public Task CompleteAsync()
        {
            CompleteRequested = true;
            return Task.CompletedTask;
        }

        public async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (writer is not null) await writer.FlushAsync(cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        public async Task CopyToAsync(Stream destination, CancellationToken cancellationToken)
        {
            stream.Position = 0;
            await stream.CopyToAsync(destination, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (writer is not null) await writer.CompleteAsync();
            await stream.DisposeAsync();
        }
    }

    private sealed class BoundedResponseStream(int maximumBytes) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacity(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacity(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            EnsureCapacity(1);
            base.WriteByte(value);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            EnsureCapacity(count);
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            EnsureCapacity(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }

        public override void SetLength(long value)
        {
            if (value > maximumBytes) throw TooLarge();
            base.SetLength(value);
        }

        private void EnsureCapacity(int writeLength)
        {
            if (Position + writeLength > maximumBytes) throw TooLarge();
        }

        private static InvalidOperationException TooLarge() =>
            new("The mutation response exceeded the configured atomic response limit.");
    }
}
