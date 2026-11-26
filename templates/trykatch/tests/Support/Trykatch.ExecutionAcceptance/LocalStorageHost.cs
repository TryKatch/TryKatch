using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Trykatch.Infrastructure.Storage;
using Trykatch.Modules;

namespace Trykatch.ExecutionAcceptance;

/// <summary>Independent process driver for the real local adapter and its filesystem protocol.</summary>
internal static class LocalStorageHost
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task<int> RunAsync()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        ServiceCollection services = new();
        services.AddSingleton(configuration);
        services.AddStorageModule(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();
        IConditionalObjectStorage storage = provider.GetRequiredService<IConditionalObjectStorage>();
        IObjectStorage raw = provider.GetRequiredService<IObjectStorage>();
        FileStream? heldLock = null;
        GatePayload? gate = null;
        Task<ConditionalObjectWriteResult>? pending = null;
        try
        {
            while (await Console.In.ReadLineAsync() is { } line)
            {
                Request request = JsonSerializer.Deserialize<Request>(line, Json) ?? throw new InvalidDataException();
                switch (request.Kind)
                {
                    case "create":
                        await WriteAsync(new { kind = "write", result = await storage.TryCreateAsync(request.Key,
                            new MemoryStream(request.Payload!), request.Payload!.LongLength, "application/octet-stream", request.Identity!), processId = Environment.ProcessId });
                        break;
                    case "gate-create":
                        gate = new(request.Payload!);
                        pending = storage.TryCreateAsync(request.Key, gate, request.Payload!.LongLength,
                            "application/octet-stream", request.Identity!);
                        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                        await WriteAsync(new { kind = "spooling", processId = Environment.ProcessId });
                        break;
                    case "release":
                        gate!.Release.TrySetResult();
                        await WriteAsync(new { kind = "write", result = await pending!, processId = Environment.ProcessId });
                        await gate.DisposeAsync();
                        gate = null;
                        pending = null;
                        break;
                    case "stat":
                        await WriteAsync(new { kind = "stat", snapshot = await storage.StatAsync(request.Key),
                            fingerprint = storage.NamespaceFingerprint, processId = Environment.ProcessId });
                        break;
                    case "cancel":
                        await WriteAsync(new { kind = "write", result = await storage.TryReplaceWithCancellationAsync(
                            request.Key, request.Revision, request.Identity!), processId = Environment.ProcessId });
                        break;
                    case "get":
                        await using (Stream content = await raw.GetAsync(request.Key))
                        {
                            using MemoryStream bytes = new();
                            await content.CopyToAsync(bytes);
                            await WriteAsync(new { kind = "payload", payload = bytes.ToArray(), length = content.Length, processId = Environment.ProcessId });
                        }
                        break;
                    case "hold-key-lock":
                        // Deliberately hold the documented permanent inode from another OS process.
                        string root = configuration["Storage:LocalPath"]!;
                        string path = Path.Combine(root, "_module-uploads", "v1", ".locks",
                            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.Key))) + ".lock");
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        heldLock = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                        await WriteAsync(new { kind = "locked", processId = Environment.ProcessId });
                        break;
                    default: throw new InvalidDataException();
                }
            }
            return 0;
        }
        catch (Exception failure)
        {
            await WriteAsync(new { kind = "error", code = failure.GetType().Name, processId = Environment.ProcessId });
            return 1;
        }
        finally
        {
            if (gate is not null)
            {
                gate.Release.TrySetResult();
                if (pending is not null) await pending;
                await gate.DisposeAsync();
            }
            if (heldLock is not null) await heldLock.DisposeAsync();
        }
    }

    private static async Task WriteAsync<T>(T value)
    {
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(value, Json));
        await Console.Out.FlushAsync();
    }

    private sealed record Request(string Kind, string Key, byte[]? Payload, ObjectWriteIdentity? Identity, ObjectStorageRevision? Revision);

    private sealed class GatePayload(byte[] payload) : Stream
    {
        private readonly MemoryStream source = new(payload);
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await source.ReadAsync(buffer, cancellationToken);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) source.Dispose();
            base.Dispose(disposing);
        }
    }
}
