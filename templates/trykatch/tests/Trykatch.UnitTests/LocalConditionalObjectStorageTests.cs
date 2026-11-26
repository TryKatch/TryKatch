using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trykatch.Infrastructure.Storage;
using Trykatch.Modules;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class LocalConditionalObjectStorageTests
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "trykatch-storage-tests", Guid.NewGuid().ToString("N"));
    private readonly string key = $"_module-uploads/v1/organizations/{Guid.NewGuid():N}/documents/{Guid.NewGuid():N}";
    private const string MetadataFingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly List<ServiceProvider> providers = [];

    [TestCleanup]
    public void Cleanup()
    {
        foreach (ServiceProvider provider in providers) provider.Dispose();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public async Task LegacyRawStorageStillRoundTripsAndDeletesOrdinaryObjects()
    {
        IObjectStorage storage = CreateStorage();
        byte[] bytes = [1, 2, 3, 4];

        await storage.PutAsync("legacy/docs/item.bin", new MemoryStream(bytes), bytes.Length, "application/octet-stream");
        await using (Stream read = await storage.GetAsync("legacy/docs/item.bin"))
        {
            using MemoryStream copy = new();
            await read.CopyToAsync(copy);
            copy.ToArray().ShouldBe(bytes);
        }

        await storage.DeleteAsync("legacy/docs/item.bin");
        File.Exists(Path.Combine(root, "legacy", "docs", "item.bin")).ShouldBeFalse();
    }

    [TestMethod]
    public async Task CreatePublishesExactPayloadAndIndependentStat()
    {
        (IObjectStorage raw, IConditionalObjectStorage storage) = CreateStorages();
        byte[] bytes = [0, 1, 2, 255];
        ObjectWriteIdentity identity = Identity(bytes);

        ConditionalObjectWriteResult result = await storage.TryCreateAsync(Key(), new MemoryStream(bytes), bytes.Length,
            "application/octet-stream", identity);

        result.Disposition.ShouldBe(ConditionalObjectWriteDisposition.Written);
        ConditionalObjectStat stat = await storage.StatAsync(Key());
        stat.State.ShouldBe(ConditionalObjectState.Provisional);
        stat.Length.ShouldBe(bytes.Length);
        stat.Identity.ShouldBe(identity);
        stat.Revision.ShouldNotBeNull();
        await using Stream independent = await raw.GetAsync(Key());
        independent.Length.ShouldBe(bytes.Length);
        independent.Position.ShouldBe(0);
        independent.Seek(1, SeekOrigin.Begin).ShouldBe(1);
        independent.ReadByte().ShouldBe(bytes[1]);
        independent.Position = 0;
        using MemoryStream actual = new();
        await independent.CopyToAsync(actual);
        actual.ToArray().ShouldBe(bytes);
    }

    [TestMethod]
    public async Task MatchingCreateIsIdempotentAndDifferentIdentityCannotOverwrite()
    {
        IConditionalObjectStorage storage = CreateConditional();
        byte[] bytes = [4, 5, 6];
        ObjectWriteIdentity identity = Identity(bytes);
        await storage.TryCreateAsync(Key(), new MemoryStream(bytes), bytes.Length, "application/octet-stream", identity);

        ConditionalObjectWriteResult retry = await storage.TryCreateAsync(Key(), new MemoryStream(bytes), bytes.Length,
            "application/octet-stream", identity);
        retry.Disposition.ShouldBe(ConditionalObjectWriteDisposition.AlreadyExists);

        ObjectWriteIdentity other = identity with { OperationId = Guid.NewGuid() };
        ConditionalObjectWriteResult conflict = await storage.TryCreateAsync(Key(), new MemoryStream([8, 9]), 2,
            "application/octet-stream", Identity([8, 9], other.OperationId));
        conflict.Disposition.ShouldBe(ConditionalObjectWriteDisposition.Conflict);
        ConditionalObjectStat stat = await storage.StatAsync(Key());
        stat.Identity.ShouldBe(identity);
        stat.Length.ShouldBe(bytes.Length);
    }

    [TestMethod]
    public async Task DeclaredLengthActualLengthAndDigestMustAllMatchBeforePublish()
    {
        byte[] bytes = [10, 11, 12];
        foreach ((long declared, byte[] actual, string hash) in new[]
        {
            (2L, bytes, Hash(bytes)),
            (4L, bytes, Hash(bytes)),
            (3L, bytes, new string('0', 64))
        })
        {
            IConditionalObjectStorage storage = CreateConditional();
            ObjectWriteIdentity identity = new(Guid.NewGuid(), declared, hash, MetadataFingerprint);
            ConditionalObjectWriteResult result = await storage.TryCreateAsync(Key(), new MemoryStream(actual), declared,
                "application/octet-stream", identity);

            result.Disposition.ShouldBe(ConditionalObjectWriteDisposition.InvalidPayload);
            (await storage.StatAsync(Key())).State.ShouldBe(ConditionalObjectState.Missing);
        }
    }

    [TestMethod]
    public async Task NonSeekableStreamIsBoundedAtDeclaredLengthPlusOne()
    {
        IConditionalObjectStorage storage = CreateConditional();
        byte[] actual = [1, 2, 3, 4, 5];
        NonSeekableStream content = new(actual);
        ConditionalObjectWriteResult result = await storage.TryCreateAsync(Key(), content, 4,
            "application/octet-stream", new(Guid.NewGuid(), 4, Hash(actual), MetadataFingerprint));

        result.Disposition.ShouldBe(ConditionalObjectWriteDisposition.InvalidPayload);
        content.BytesRead.ShouldBeLessThanOrEqualTo(5);
        (await storage.StatAsync(Key())).State.ShouldBe(ConditionalObjectState.Missing);
    }

    [TestMethod]
    public async Task InvalidKeysAndIdentitiesAreRejectedWithoutCreatingObjects()
    {
        IConditionalObjectStorage storage = CreateConditional();
        byte[] bytes = [60, 61];
        ObjectWriteIdentity validIdentity = Identity(bytes);
        foreach (string invalidKey in new[] { "", "../escape", "a\\b", ".hidden/key", "non-ascii/é", new string('a', 501) })
        {
            ConditionalObjectWriteResult result = await storage.TryCreateAsync(invalidKey, new MemoryStream(bytes),
                bytes.Length, "application/octet-stream", validIdentity);
            result.Disposition.ShouldBe(ConditionalObjectWriteDisposition.InvalidPayload);
            (await storage.StatAsync(invalidKey)).State.ShouldBe(ConditionalObjectState.Unowned);
        }

        ObjectWriteIdentity[] invalidIdentities =
        [
            validIdentity with { OperationId = Guid.Empty },
            validIdentity with { OriginalLength = -1 },
            validIdentity with { Sha256 = "bad" },
            validIdentity with { MetadataFingerprint = "bad" }
        ];
        foreach (ObjectWriteIdentity invalidIdentity in invalidIdentities)
        {
            ConditionalObjectWriteResult result = await storage.TryCreateAsync(Key(), new MemoryStream(bytes),
                bytes.Length, "application/octet-stream", invalidIdentity);
            result.Disposition.ShouldBe(ConditionalObjectWriteDisposition.InvalidPayload);
        }
        (await storage.StatAsync(Key())).State.ShouldBe(ConditionalObjectState.Missing);
        Directory.Exists(root).ShouldBeFalse();
    }

    [TestMethod]
    public async Task StaleRevisionAndForeignIdentityCannotReplaceObject()
    {
        IConditionalObjectStorage storage = CreateConditional();
        byte[] bytes = [3, 2, 1];
        ObjectWriteIdentity identity = Identity(bytes);
        await storage.TryCreateAsync(Key(), new MemoryStream(bytes), bytes.Length, "application/octet-stream", identity);
        ObjectStorageRevision revision = (await storage.StatAsync(Key())).Revision!;

        ConditionalObjectWriteResult stale = await storage.TryReplaceWithCancellationAsync(Key(),
            new ObjectStorageRevision("stale-revision"), identity);
        stale.Disposition.ShouldBe(ConditionalObjectWriteDisposition.Conflict);
        ConditionalObjectWriteResult foreign = await storage.TryReplaceWithCancellationAsync(Key(), revision,
            identity with { OperationId = Guid.NewGuid() });
        foreign.Disposition.ShouldBe(ConditionalObjectWriteDisposition.Conflict);

        ConditionalObjectStat unchanged = await storage.StatAsync(Key());
        unchanged.State.ShouldBe(ConditionalObjectState.Provisional);
        unchanged.Length.ShouldBe(bytes.Length);
        unchanged.Identity.ShouldBe(identity);
    }

    [TestMethod]
    public async Task AbsentFenceCreatesPermanentSentinelThatRejectsLateCreate()
    {
        IConditionalObjectStorage storage = CreateConditional();
        byte[] bytes = [20, 21, 22];
        ObjectWriteIdentity identity = Identity(bytes);

        ConditionalObjectWriteResult fenced = await storage.TryReplaceWithCancellationAsync(Key(), null, identity);
        fenced.Disposition.ShouldBe(ConditionalObjectWriteDisposition.Written);
        ConditionalObjectStat sentinel = await storage.StatAsync(Key());
        sentinel.State.ShouldBe(ConditionalObjectState.Cancellation);
        sentinel.Length.ShouldBe(0);
        sentinel.Identity.ShouldBe(identity);

        ConditionalObjectWriteResult late = await storage.TryCreateAsync(Key(), new MemoryStream(bytes), bytes.Length,
            "application/octet-stream", identity);
        late.Disposition.ShouldBe(ConditionalObjectWriteDisposition.AlreadyExists);
        (await storage.StatAsync(Key())).ShouldBe(sentinel);
    }

    [TestMethod]
    public async Task OwnedProvisionalIsReplacedAtExactRevisionAndRetainsOriginalLength()
    {
        IConditionalObjectStorage storage = CreateConditional();
        byte[] bytes = [30, 31, 32, 33];
        ObjectWriteIdentity identity = Identity(bytes);
        await storage.TryCreateAsync(Key(), new MemoryStream(bytes), bytes.Length, "application/octet-stream", identity);
        ObjectStorageRevision revision = (await storage.StatAsync(Key())).Revision!;

        ConditionalObjectWriteResult fenced = await storage.TryReplaceWithCancellationAsync(Key(), revision, identity);
        fenced.Disposition.ShouldBe(ConditionalObjectWriteDisposition.Written);
        ConditionalObjectStat stat = await storage.StatAsync(Key());
        stat.State.ShouldBe(ConditionalObjectState.Cancellation);
        stat.Length.ShouldBe(0);
        stat.Identity.ShouldBe(identity);
        stat.Identity!.OriginalLength.ShouldBe(bytes.Length);

        ConditionalObjectWriteResult duplicate = await storage.TryReplaceWithCancellationAsync(Key(), revision,
            identity);
        duplicate.Disposition.ShouldBe(ConditionalObjectWriteDisposition.AlreadyExists);
    }

    [TestMethod]
    public async Task MalformedManagedObjectIsUnownedAndNeverMutated()
    {
        IConditionalObjectStorage storage = CreateConditional();
        string path = ManagedPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "not-an-envelope");

        ConditionalObjectStat stat = await storage.StatAsync(Key());
        stat.State.ShouldBe(ConditionalObjectState.Unowned);
        ConditionalObjectWriteResult result = await storage.TryReplaceWithCancellationAsync(Key(), null,
            Identity([1]));
        result.Disposition.ShouldBe(ConditionalObjectWriteDisposition.Conflict);
        (await File.ReadAllTextAsync(path)).ShouldBe("not-an-envelope");
    }

    [TestMethod]
    public async Task RawPutAndDeleteRefuseReservedNamespaceAndCaseAliasesBeforeIo()
    {
        IObjectStorage storage = CreateStorage();
        foreach (string key in new[]
        {
            Key(), "_module-uploads/absent", "/_MODULE-UPLOADS/v1/organizations/x/documents/y",
            "prefix/../_module-uploads/v1/organizations/x/documents/y"
        })
        {
            await Should.ThrowAsync<InvalidOperationException>(() => storage.PutAsync(key, new MemoryStream([1]), 1,
                "application/octet-stream"));
            await Should.ThrowAsync<InvalidOperationException>(() => storage.DeleteAsync(key));
        }
        Directory.Exists(Path.Combine(root, "_module-uploads")).ShouldBeFalse();
    }

    [TestMethod]
    public async Task CancelledCallPropagatesCancellation()
    {
        IConditionalObjectStorage storage = CreateConditional();
        using CancellationTokenSource source = new();
        source.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => storage.TryCreateAsync(Key(), new MemoryStream([1]),
            1, "application/octet-stream", Identity([1]), source.Token));
    }

    [TestMethod]
    public void NamespaceFingerprintIsCapturedAndDiffersForDifferentRoots()
    {
        Dictionary<string, string?> values = new() { ["Storage:LocalPath"] = root };
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        IConditionalObjectStorage first = CreateConditional(configuration);
        string captured = first.NamespaceFingerprint;
        configuration["Storage:LocalPath"] = Path.Combine(root, "changed");

        first.NamespaceFingerprint.ShouldBe(captured);
        CreateConditional(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:LocalPath"] = root + Path.DirectorySeparatorChar
        }).Build()).NamespaceFingerprint.ShouldBe(captured);
        CreateConditional(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:LocalPath"] = Path.Combine(root, "other")
        }).Build()).NamespaceFingerprint.ShouldNotBe(captured);
    }

    [TestMethod]
    public async Task ConcurrentCreatesHaveExactlyOneWriter()
    {
        IConditionalObjectStorage storage = CreateConditional();
        byte[] bytes = [40, 41, 42];
        ObjectWriteIdentity identity = Identity(bytes);
        Task<ConditionalObjectWriteResult>[] writes = Enumerable.Range(0, 8).Select(_ =>
            storage.TryCreateAsync(Key(), new MemoryStream(bytes), bytes.Length, "application/octet-stream", identity)).ToArray();

        ConditionalObjectWriteResult[] results = await Task.WhenAll(writes);
        results.Count(x => x.Disposition == ConditionalObjectWriteDisposition.Written).ShouldBe(1);
        results.ShouldAllBe(x => x.Disposition == ConditionalObjectWriteDisposition.Written
            || x.Disposition == ConditionalObjectWriteDisposition.AlreadyExists);
        (await storage.StatAsync(Key())).State.ShouldBe(ConditionalObjectState.Provisional);
    }

    [TestMethod]
    public async Task FenceDuringSlowSpoolingWinsAgainstLatePublication()
    {
        IConditionalObjectStorage storage = CreateConditional();
        byte[] bytes = [50, 51, 52];
        ObjectWriteIdentity identity = Identity(bytes);
        using GateStream content = new(bytes);
        Task<ConditionalObjectWriteResult> write = storage.TryCreateAsync(Key(), content, bytes.Length,
            "application/octet-stream", identity);
        await content.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        ConditionalObjectWriteResult fence = await storage.TryReplaceWithCancellationAsync(Key(), null, identity);
        fence.Disposition.ShouldBe(ConditionalObjectWriteDisposition.Written);
        content.ReleaseRead.TrySetResult();

        ConditionalObjectWriteResult late = await write.WaitAsync(TimeSpan.FromSeconds(5));
        late.Disposition.ShouldBe(ConditionalObjectWriteDisposition.AlreadyExists);
        ConditionalObjectStat stat = await storage.StatAsync(Key());
        stat.State.ShouldBe(ConditionalObjectState.Cancellation);
        stat.Length.ShouldBe(0);
        stat.Identity.ShouldBe(identity);
    }

    private IObjectStorage CreateStorage() => CreateStorages().Raw;
    private IConditionalObjectStorage CreateConditional() => CreateStorages().Conditional;

    private (IObjectStorage Raw, IConditionalObjectStorage Conditional) CreateStorages() =>
        CreateStorages(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:LocalPath"] = root
        }).Build());

    private (IObjectStorage Raw, IConditionalObjectStorage Conditional) CreateStorages(IConfiguration configuration)
    {
        ServiceCollection services = new();
        services.AddSingleton(configuration);
        services.AddStorageModule(configuration);
        ServiceProvider provider = services.BuildServiceProvider();
        providers.Add(provider);
        IObjectStorage raw = provider.GetRequiredService<IObjectStorage>();
        return (raw, (IConditionalObjectStorage)raw);
    }

    private IConditionalObjectStorage CreateConditional(IConfiguration configuration) =>
        (IConditionalObjectStorage)CreateStorages(configuration).Raw;

    private string ManagedPath() => Path.Combine(root, Key().Replace('/', Path.DirectorySeparatorChar));
    private string Key() => key;
    private static ObjectWriteIdentity Identity(byte[] bytes, Guid? operationId = null) =>
        new(operationId ?? Guid.NewGuid(), bytes.LongLength, Hash(bytes), MetadataFingerprint);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class NonSeekableStream(byte[] content) : Stream
    {
        private readonly MemoryStream inner = new(content);
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await inner.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class GateStream(byte[] content) : Stream
    {
        private readonly MemoryStream inner = new(content);
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await ReleaseRead.Task.WaitAsync(cancellationToken);
            return await inner.ReadAsync(buffer, cancellationToken);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
