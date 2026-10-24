using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using System.IO.Pipelines;
using Trykatch.Modules;

namespace Trykatch.Modules.AspNetCore;

/// <summary>
/// Stages a bounded mutation response until the owning application transaction
/// has committed or rolled back. This never invokes the downstream pipeline twice.
/// </summary>
public static class AtomicMutationResponse
{
    /// <summary>Stages application response-start callbacks until an atomic response is ready to publish.</summary>
    public static async Task StageStartingCallbacksAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        if (context.Features.Get<StagedResponseStartingFeature>() is not null)
        {
            await next(context);
            return;
        }

        IHttpResponseFeature original = context.Features.Get<IHttpResponseFeature>()
            ?? throw new InvalidOperationException("The HTTP response feature is unavailable.");
        InstallStartingFeature(context, original);
        try
        {
            await next(context);
        }
        finally
        {
            context.Features.Set<IHttpResponseFeature>(original);
            context.Features.Set<StagedResponseStartingFeature>(null);
        }
    }

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
        StagedResponseStartingFeature? existingStarting = context.Features.Get<StagedResponseStartingFeature>();
        bool ownsStartingFeature = existingStarting is null;
        IHttpResponseFeature? originalResponse = null;
        StagedResponseStartingFeature stagedStarting;
        if (existingStarting is not null)
        {
            stagedStarting = existingStarting;
        }
        else
        {
            originalResponse = context.Features.Get<IHttpResponseFeature>()
                ?? throw new InvalidOperationException("The HTTP response feature is unavailable.");
            stagedStarting = InstallStartingFeature(context, originalResponse);
        }

        await using StagedResponseBodyFeature staged = new(maximumBytes);
        ModuleAtomicMutationResponseLimit? previousLimit = context.Features.Get<ModuleAtomicMutationResponseLimit>();
        context.Features.Set(new ModuleAtomicMutationResponseLimit(maximumBytes));
        context.Features.Set<IHttpResponseBodyFeature>(staged);
        bool transactionFinalized = false;
        try
        {
            await next(context);
            await staged.FlushAsync(context.RequestAborted);
            await stagedStarting.FireStartingCallbacksAsync();
            await staged.FlushAsync(context.RequestAborted);

            if (context.Features.Get<ModuleMutationReplayResponse>() is { } replay)
            {
                MutationReceipt receipt = replay.Receipt;
                context.Response.StatusCode = receipt.StatusCode;
                context.Response.ContentType = receipt.ContentType;
                if (receipt.Location is null)
                    context.Response.Headers.Remove("Location");
                else
                    context.Response.Headers.Location = receipt.Location;

                stagedStarting.Freeze();
                byte[] responseBody = staged.SealAndGetBody();
                if (!responseBody.AsSpan().SequenceEqual(receipt.Body))
                    throw new InvalidOperationException("The replay response body changed while starting the response.");
            }
            else if (context.Features.Get<IModuleMutationResponseCaptureFeature>() is { } capture)
            {
                stagedStarting.Freeze();
                StagedMutationResponse snapshot = staged.Capture(context.Response, capture.MaximumResponseBytes);
                await capture.CaptureAsync(snapshot, context.RequestAborted);
                context.RequestAborted.ThrowIfCancellationRequested();
            }

            await finalizeTransaction(context.RequestAborted);
            transactionFinalized = true;
            await clientBody.StartAsync(context.RequestAborted);
            context.Features.Set<IHttpResponseBodyFeature>(clientBody);
            await staged.CopyToAsync(clientBody.Stream, context.RequestAborted);
            if (staged.CompleteRequested) await clientBody.CompleteAsync();
        }
        finally
        {
            if (!transactionFinalized)
                stagedStarting.Unfreeze();
            context.Features.Set<IHttpResponseBodyFeature>(clientBody);
            context.Features.Set(previousLimit);
            if (ownsStartingFeature && originalResponse is not null)
            {
                context.Features.Set<IHttpResponseFeature>(originalResponse);
                context.Features.Set<StagedResponseStartingFeature>(null);
            }
        }
    }

    private static StagedResponseStartingFeature InstallStartingFeature(HttpContext context, IHttpResponseFeature original)
    {
        StagedResponseStartingFeature staged = new(original);
        staged.RegisterNativeFallback();
        context.Features.Set(staged);
        context.Features.Set<IHttpResponseFeature>(staged);
        return staged;
    }

    private sealed class StagedResponseBodyFeature(int maximumBytes) : IHttpResponseBodyFeature, IAsyncDisposable
    {
        private readonly BoundedResponseStream stream = new(maximumBytes);
        private PipeWriter? writer;
        private bool streamingRequested;
        private bool fileRequested;

        public Stream Stream => stream;
        public PipeWriter Writer => writer ??= PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
        public bool CompleteRequested { get; private set; }

        public void DisableBuffering() => streamingRequested = true;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default)
        {
            fileRequested = true;
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

        public StagedMutationResponse Capture(HttpResponse response, int maximumResponseBytes)
        {
            if (streamingRequested || fileRequested)
                throw new NotSupportedException("Streaming and file responses cannot be captured for mutation replay.");
            if (maximumResponseBytes < 1 || maximumResponseBytes > maximumBytes || stream.Length > maximumResponseBytes)
                throw new InvalidOperationException("The response exceeds its configured mutation replay limit.");
            if (response.Headers.Location.Count > 1)
                throw new InvalidOperationException("A mutation replay cannot capture multiple Location headers.");
            stream.Seal();
            string? location = response.Headers.Location.Count == 0 ? null : response.Headers.Location.ToString();
            return new(response.StatusCode, response.ContentType, location, stream.ToArray());
        }

        public byte[] SealAndGetBody()
        {
            if (streamingRequested || fileRequested)
                throw new NotSupportedException("Streaming and file responses cannot be replayed.");
            stream.Seal();
            return stream.ToArray();
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
        private bool sealedForCapture;

        public void Seal() => sealedForCapture = true;

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
            EnsureWritable();
            if (value > maximumBytes) throw TooLarge();
            base.SetLength(value);
        }

        private void EnsureCapacity(int writeLength)
        {
            EnsureWritable();
            if (Position + writeLength > maximumBytes) throw TooLarge();
        }

        private void EnsureWritable()
        {
            if (sealedForCapture)
                throw new InvalidOperationException("The staged mutation response is sealed for capture.");
        }

        private static InvalidOperationException TooLarge() =>
            new("The mutation response exceeded the configured atomic response limit.");
    }
}

internal sealed record ModuleAtomicMutationResponseLimit(int MaximumBytes);
