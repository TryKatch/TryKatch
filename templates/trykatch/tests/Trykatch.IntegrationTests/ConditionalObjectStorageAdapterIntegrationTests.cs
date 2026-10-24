#if TRYKATCH_STORAGE
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using Trykatch.Infrastructure.Storage;
using Trykatch.Modules;

namespace Trykatch.IntegrationTests;

[TestClass]
public sealed class ConditionalObjectStorageAdapterIntegrationTests
{
    private const string AccessKey = "trykatch-object-storage-test";
    private const string SecretKey = "trykatch-object-storage-test-secret";
    private const string Image =
        "docker.io/rustfs/rustfs:1.0.3@sha256:9ca9559533b537d10482fe923288b676ad08f92a7069d9a17327fbae3cab688b";

    private static IContainer _container = null!;
    private static ServiceProvider _provider = null!;
    private static IConfiguration _configuration = null!;
    private static AmazonS3Client _client = null!;
    private static string _serviceUrl = null!;
    private static string _bucket = null!;
    private static IConditionalObjectStorage _conditional = null!;
    private static IObjectStorage _raw = null!;

    [ClassInitialize]
    public static async Task StartStorage(TestContext _)
    {
        _container = new ContainerBuilder(Image)
            .WithEnvironment("RUSTFS_ACCESS_KEY", AccessKey)
            .WithEnvironment("RUSTFS_SECRET_KEY", SecretKey)
            .WithEnvironment("RUSTFS_CONSOLE_ENABLE", "false")
            .WithEnvironment("RUSTFS_ADDRESS", ":9000")
            .WithPortBinding(9000, assignRandomHostPort: true)
            .WithCommand("/data")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request =>
                request.ForPort(9000).ForPath("/health/ready")))
            .Build();
        await _container.StartAsync();

        _serviceUrl = $"http://127.0.0.1:{_container.GetMappedPublicPort(9000)}";
        _bucket = $"conditional-adapter-{Guid.NewGuid():N}";
        _configuration = CreateConfiguration(_serviceUrl, _bucket);
        ServiceCollection services = new();
        services.AddSingleton(_configuration);
        services.AddStorageModule(_configuration);
        _provider = services.BuildServiceProvider();
        _conditional = _provider.GetRequiredService<IConditionalObjectStorage>();
        _raw = _provider.GetRequiredService<IObjectStorage>();
        _raw.ShouldBeSameAs(_conditional);
        _client = new AmazonS3Client(new BasicAWSCredentials(AccessKey, SecretKey), CreateS3Config(_serviceUrl));
        await _client.PutBucketAsync(_bucket);
    }

    [ClassCleanup]
    public static async Task StopStorage()
    {
        _client?.Dispose();
        if (_provider is not null) await _provider.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }

    [TestMethod]
    public async Task CreateIsVerifiedInStorageAndStatReportsHeadIdentityAndLength()
    {
        string key = Key();
        byte[] bytes = Encoding.UTF8.GetBytes("adapter verified object bytes");
        ObjectWriteIdentity identity = Identity(bytes) with { Sha256 = Hash(bytes).ToUpperInvariant() };

        ConditionalObjectWriteResult created = await _conditional.TryCreateAsync(key, new MemoryStream(bytes), bytes.Length,
            "application/octet-stream", identity);

        created.Disposition.ShouldBe(ConditionalObjectWriteDisposition.Written);
        ConditionalObjectStat stat = await _conditional.StatAsync(key);
        stat.State.ShouldBe(ConditionalObjectState.Provisional);
        stat.Length.ShouldBe(bytes.LongLength);
        AssertIdentity(stat.Identity, identity);
        stat.Revision.ShouldNotBeNull();

        await using Stream downloaded = await _raw.GetAsync(key);
        using MemoryStream actual = new();
        await downloaded.CopyToAsync(actual);
        actual.ToArray().ShouldBe(bytes);
        Hash(actual.ToArray()).ShouldBe(Hash(bytes));
    }

    [TestMethod]
    public async Task MatchingCreateIsAlreadyExistsAndCompetingIdentityCannotOverwriteWinner()
    {
        string key = Key();
        byte[] firstBytes = Encoding.UTF8.GetBytes("first immutable writer");
        byte[] secondBytes = Encoding.UTF8.GetBytes("second immutable writer");
        ObjectWriteIdentity first = Identity(firstBytes);
        ObjectWriteIdentity second = Identity(secondBytes);

        ConditionalObjectWriteResult[] results = await Task.WhenAll(
            _conditional.TryCreateAsync(key, new MemoryStream(firstBytes), firstBytes.Length, "text/plain", first),
            _conditional.TryCreateAsync(key, new MemoryStream(secondBytes), secondBytes.Length, "text/plain", second));

        results.Count(result => result.Disposition == ConditionalObjectWriteDisposition.Written).ShouldBe(1);
        results.Count(result => result.Disposition == ConditionalObjectWriteDisposition.Conflict).ShouldBe(1);
        ConditionalObjectStat winner = await _conditional.StatAsync(key);
        winner.State.ShouldBe(ConditionalObjectState.Provisional);
        ObjectWriteIdentity winningIdentity = results.Single(result => result.Disposition == ConditionalObjectWriteDisposition.Written)
            .Snapshot!.Identity!;
        AssertIdentity(winner.Identity, winningIdentity);

        byte[] winningBytes = winningIdentity.OperationId == first.OperationId ? firstBytes : secondBytes;
        ConditionalObjectWriteResult retry = await _conditional.TryCreateAsync(key, new MemoryStream(winningBytes),
            winningBytes.Length, "text/plain", winningIdentity);
        retry.Disposition.ShouldBe(ConditionalObjectWriteDisposition.AlreadyExists);

        await using Stream downloaded = await _raw.GetAsync(key);
        using MemoryStream actual = new();
        await downloaded.CopyToAsync(actual);
        actual.ToArray().ShouldBe(winningBytes);
    }

    [TestMethod]
    public async Task InvalidLengthHashOrIdentityDoesNotCreateObject()
    {
        byte[] bytes = new byte[] { 1, 2, 3, 4 };
        (long Length, string Hash, Guid OperationId, string Fingerprint)[] invalid = new (long Length, string Hash, Guid OperationId, string Fingerprint)[]
        {
            (3, Hash(bytes), Guid.NewGuid(), Fingerprint()),
            (5, Hash(bytes), Guid.NewGuid(), Fingerprint()),
            (bytes.Length, new string('0', 64), Guid.NewGuid(), Fingerprint()),
            (bytes.Length, "not-a-sha256", Guid.NewGuid(), Fingerprint()),
            (0, Hash(Array.Empty<byte>()), Guid.NewGuid(), Fingerprint()),
            (bytes.Length, Hash(bytes), Guid.Empty, Fingerprint()),
            (bytes.Length, Hash(bytes), Guid.NewGuid(), "invalid-metadata-fingerprint")
        };

        foreach ((long length, string hash, Guid operationId, string fingerprint) in invalid)
        {
            string key = Key();
            ConditionalObjectWriteResult result = await _conditional.TryCreateAsync(key, new MemoryStream(bytes), length,
                "application/octet-stream", new ObjectWriteIdentity(operationId, length, hash, fingerprint));

            result.Disposition.ShouldBe(ConditionalObjectWriteDisposition.InvalidPayload);
            (await _conditional.StatAsync(key)).State.ShouldBe(ConditionalObjectState.Missing);
        }
    }

    [TestMethod]
    public async Task CancelledCallerTokenPropagatesWithoutCreatingObject()
    {
        string key = Key();
        byte[] bytes = new byte[] { 4, 5, 6 };
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => _conditional.TryCreateAsync(key,
            new MemoryStream(bytes), bytes.Length, "application/octet-stream", Identity(bytes), cancellation.Token));
        (await _conditional.StatAsync(key)).State.ShouldBe(ConditionalObjectState.Missing);
    }

    [TestMethod]
    public async Task FenceOnMissingObjectCreatesSentinelThatBlocksLaterCreate()
    {
        string key = Key();
        byte[] bytes = Encoding.UTF8.GetBytes("late provisional payload");
        ObjectWriteIdentity identity = Identity(bytes);

        ConditionalObjectWriteResult fence = await _conditional.TryReplaceWithCancellationAsync(key, null, identity);
        fence.Disposition.ShouldBe(ConditionalObjectWriteDisposition.Written);
        ConditionalObjectStat sentinel = await _conditional.StatAsync(key);
        sentinel.State.ShouldBe(ConditionalObjectState.Cancellation);
        sentinel.Length.ShouldBe(0);
        AssertIdentity(sentinel.Identity, identity);

        ConditionalObjectWriteResult delayed = await _conditional.TryCreateAsync(key, new MemoryStream(bytes), bytes.Length,
            "application/octet-stream", identity);
        delayed.Disposition.ShouldBe(ConditionalObjectWriteDisposition.AlreadyExists);
        (await _conditional.StatAsync(key)).ShouldBe(sentinel);
    }

    [TestMethod]
    public async Task FenceDuringDelayedCreateLeavesCancellationSentinel()
    {
        string key = Key();
        byte[] bytes = Encoding.UTF8.GetBytes("write whose body arrives late");
        ObjectWriteIdentity identity = Identity(bytes);
        using GateStream delayedContent = new(bytes);

        Task<ConditionalObjectWriteResult> delayedWrite = _conditional.TryCreateAsync(key, delayedContent, bytes.Length,
            "application/octet-stream", identity);
        await delayedContent.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        ConditionalObjectWriteResult fence = await _conditional.TryReplaceWithCancellationAsync(key, null, identity);
        fence.Disposition.ShouldBe(ConditionalObjectWriteDisposition.Written);
        delayedContent.ReleaseRead.TrySetResult();

        (await delayedWrite.WaitAsync(TimeSpan.FromSeconds(10))).Disposition.ShouldBe(ConditionalObjectWriteDisposition.AlreadyExists);
        ConditionalObjectStat final = await _conditional.StatAsync(key);
        final.State.ShouldBe(ConditionalObjectState.Cancellation);
        final.Length.ShouldBe(0);
        AssertIdentity(final.Identity, identity);
    }

    [TestMethod]
    public async Task FenceReplacesOnlyExactOwnedRevisionAndPreservesOriginalIdentity()
    {
        string key = Key();
        byte[] bytes = Encoding.UTF8.GetBytes("owned provisional object");
        ObjectWriteIdentity identity = Identity(bytes);
        (await _conditional.TryCreateAsync(key, new MemoryStream(bytes), bytes.Length, "text/plain", identity))
            .Disposition.ShouldBe(ConditionalObjectWriteDisposition.Written);
        ObjectStorageRevision revision = (await _conditional.StatAsync(key)).Revision!;

        ConditionalObjectWriteResult fenced = await _conditional.TryReplaceWithCancellationAsync(key, revision, identity);

        fenced.Disposition.ShouldBe(ConditionalObjectWriteDisposition.Written);
        ConditionalObjectStat sentinel = await _conditional.StatAsync(key);
        sentinel.State.ShouldBe(ConditionalObjectState.Cancellation);
        sentinel.Length.ShouldBe(0);
        AssertIdentity(sentinel.Identity, identity);
        sentinel.Identity!.OriginalLength.ShouldBe(bytes.LongLength);
        (await _conditional.TryReplaceWithCancellationAsync(key, revision, identity)).Disposition
            .ShouldBe(ConditionalObjectWriteDisposition.AlreadyExists);
    }

    [TestMethod]
    public async Task StaleRevisionOrForeignIdentityCannotChangeOwnedObject()
    {
        string key = Key();
        byte[] bytes = Encoding.UTF8.GetBytes("keep this provisional object");
        ObjectWriteIdentity identity = Identity(bytes);
        await _conditional.TryCreateAsync(key, new MemoryStream(bytes), bytes.Length, "text/plain", identity);
        ObjectStorageRevision current = (await _conditional.StatAsync(key)).Revision!;

        (await _conditional.TryReplaceWithCancellationAsync(key, new ObjectStorageRevision(current.Value + "stale"), identity))
            .Disposition.ShouldBe(ConditionalObjectWriteDisposition.Conflict);
        (await _conditional.TryReplaceWithCancellationAsync(key, current, identity with { OperationId = Guid.NewGuid() }))
            .Disposition.ShouldBe(ConditionalObjectWriteDisposition.Conflict);
        ConditionalObjectStat unchanged = await _conditional.StatAsync(key);
        unchanged.State.ShouldBe(ConditionalObjectState.Provisional);
        unchanged.Length.ShouldBe(bytes.LongLength);
        AssertIdentity(unchanged.Identity, identity);

        (await _conditional.TryReplaceWithCancellationAsync(key, current, identity)).Disposition
            .ShouldBe(ConditionalObjectWriteDisposition.Written);
        (await _conditional.TryReplaceWithCancellationAsync(key, new ObjectStorageRevision("older-version"), identity))
            .Disposition.ShouldBe(ConditionalObjectWriteDisposition.AlreadyExists);
    }

    [TestMethod]
    public async Task UnownedOrMalformedManagedMetadataIsNotExposedOrMutated()
    {
        string key = Key();
        byte[] bytes = Encoding.UTF8.GetBytes("foreign managed-prefix object");
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = new MemoryStream(bytes),
            ContentType = "text/plain"
        });

        ConditionalObjectStat before = await _conditional.StatAsync(key);
        before.State.ShouldBe(ConditionalObjectState.Unowned);
        before.Identity.ShouldBeNull();
        before.Revision.ShouldBeNull();
        (await _conditional.TryReplaceWithCancellationAsync(key, null, Identity(bytes))).Disposition
            .ShouldBe(ConditionalObjectWriteDisposition.Conflict);
        ConditionalObjectStat after = await _conditional.StatAsync(key);
        after.State.ShouldBe(ConditionalObjectState.Unowned);
        after.Identity.ShouldBeNull();
        after.Revision.ShouldBeNull();

        using GetObjectResponse stored = await _client.GetObjectAsync(_bucket, key);
        using MemoryStream actual = new();
        await stored.ResponseStream.CopyToAsync(actual);
        actual.ToArray().ShouldBe(bytes);

        string malformedKey = Key();
        PutObjectRequest malformedEnvelope = new()
        {
            BucketName = _bucket,
            Key = malformedKey,
            InputStream = new MemoryStream(bytes),
            ContentType = "text/plain"
        };
        malformedEnvelope.Metadata.Add("tk-upload-version", "1");
        malformedEnvelope.Metadata.Add("tk-upload-state", "provisional");
        await _client.PutObjectAsync(malformedEnvelope);
        ConditionalObjectStat malformed = await _conditional.StatAsync(malformedKey);
        malformed.State.ShouldBe(ConditionalObjectState.Unowned);
        malformed.Identity.ShouldBeNull();
        malformed.Revision.ShouldBeNull();
        (await _conditional.TryReplaceWithCancellationAsync(malformedKey, null, Identity(bytes))).Disposition
            .ShouldBe(ConditionalObjectWriteDisposition.Conflict);
        using GetObjectResponse malformedStored = await _client.GetObjectAsync(_bucket, malformedKey);
        using MemoryStream malformedActual = new();
        await malformedStored.ResponseStream.CopyToAsync(malformedActual);
        malformedActual.ToArray().ShouldBe(bytes);
    }

    [TestMethod]
    public async Task RawStorageRejectsEveryReservedPrefixBeforeCheckingObjectExistence()
    {
        foreach (string key in new[]
        {
            "_module-uploads/absent",
            "_module-uploads/v1/orgs/not-a-guid/objects/not-a-guid",
            "/_module-uploads/v1/orgs/not-a-guid/objects/not-a-guid",
            "_MODULE-UPLOADS/v1/orgs/not-a-guid/objects/not-a-guid"
        })
        {
            await Should.ThrowAsync<InvalidOperationException>(() => _raw.PutAsync(key, new MemoryStream(new byte[] { 7 }), 1,
                "application/octet-stream"));
            await Should.ThrowAsync<InvalidOperationException>(() => _raw.DeleteAsync(key));
        }
    }

    [TestMethod]
    public async Task InvalidManagedKeysAreUnownedForStatAndInvalidForWrites()
    {
        byte[] bytes = new byte[] { 8, 9, 10 };
        ObjectWriteIdentity identity = Identity(bytes);
        string[] invalidKeys =
        {
            "legacy/key",
            "_module-uploads/v1/",
            "_module-uploads/v1/../escaped",
            "_module-uploads/v1/./dot-segment",
            "_module-uploads/v1/back\\slash",
            "_module-uploads/v1/control\u0001character",
            $"_module-uploads/v1/{new string('x', 501)}"
        };
        foreach (string invalidKey in invalidKeys)
        {
            (await _conditional.StatAsync(invalidKey)).State.ShouldBe(ConditionalObjectState.Unowned);
            (await _conditional.TryCreateAsync(invalidKey, new MemoryStream(bytes), bytes.Length, "application/octet-stream", identity))
                .Disposition.ShouldBe(ConditionalObjectWriteDisposition.InvalidPayload);
            (await _conditional.TryReplaceWithCancellationAsync(invalidKey, null, identity))
                .Disposition.ShouldBe(ConditionalObjectWriteDisposition.InvalidPayload);
        }
    }

    [TestMethod]
    public void NamespaceFingerprintUsesCapturedDestinationAndExcludesCredentialRotation()
    {
        string capturedBucket = $"fingerprint-{Guid.NewGuid():N}";
        IConfiguration originalConfiguration = CreateConfiguration(_serviceUrl, capturedBucket);
        using ServiceProvider originalProvider = CreateProvider(originalConfiguration);
        string fingerprint = originalProvider.GetRequiredService<IConditionalObjectStorage>().NamespaceFingerprint;
        originalConfiguration["Storage:Bucket"] = $"redirect-{Guid.NewGuid():N}";
        originalConfiguration["Storage:ServiceUrl"] = "http://127.0.0.1:9001";
        originalProvider.GetRequiredService<IConditionalObjectStorage>().NamespaceFingerprint.ShouldBe(fingerprint);

        using ServiceProvider rotatedCredentialsProvider = CreateProvider(CreateConfiguration(_serviceUrl, capturedBucket,
            "rotated-access-key", "rotated-secret-key"));
        rotatedCredentialsProvider.GetRequiredService<IConditionalObjectStorage>().NamespaceFingerprint.ShouldBe(fingerprint);

        using ServiceProvider otherBucketProvider = CreateProvider(CreateConfiguration(_serviceUrl, $"other-{Guid.NewGuid():N}"));
        otherBucketProvider.GetRequiredService<IConditionalObjectStorage>().NamespaceFingerprint.ShouldNotBe(fingerprint);

        using ServiceProvider otherEndpointProvider = CreateProvider(CreateConfiguration("http://127.0.0.1:9001", capturedBucket));
        otherEndpointProvider.GetRequiredService<IConditionalObjectStorage>().NamespaceFingerprint.ShouldNotBe(fingerprint);
    }

    [TestMethod]
    public async Task CapturedBucketCannotBeRedirectedByLaterConfigurationMutation()
    {
        string capturedBucket = $"captured-{Guid.NewGuid():N}";
        string redirectedBucket = $"redirected-{Guid.NewGuid():N}";
        await _client.PutBucketAsync(capturedBucket);
        await _client.PutBucketAsync(redirectedBucket);
        IConfiguration mutableConfiguration = CreateConfiguration(_serviceUrl, capturedBucket);
        ServiceProvider provider = CreateProvider(mutableConfiguration);
        try
        {
            IConditionalObjectStorage adapter = provider.GetRequiredService<IConditionalObjectStorage>();
            mutableConfiguration["Storage:Bucket"] = redirectedBucket;
            string key = Key();
            byte[] bytes = Encoding.UTF8.GetBytes("captured endpoint and bucket");
            (await adapter.TryCreateAsync(key, new MemoryStream(bytes), bytes.Length, "text/plain", Identity(bytes)))
                .Disposition.ShouldBe(ConditionalObjectWriteDisposition.Written);
            using (GetObjectResponse fromCaptured = await _client.GetObjectAsync(capturedBucket, key))
            {
                using MemoryStream actual = new();
                await fromCaptured.ResponseStream.CopyToAsync(actual);
                actual.ToArray().ShouldBe(bytes);
            }
            AmazonS3Exception absent = await Should.ThrowAsync<AmazonS3Exception>(() => _client.GetObjectAsync(redirectedBucket, key));
            absent.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            await provider.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task LostSuccessfulCreateResponseIsUnavailableThenStatRecoversObjectAndRetryCannotOverwrite()
    {
        string key = Key();
        byte[] bytes = Encoding.UTF8.GetBytes("committed even though its response was discarded");
        ObjectWriteIdentity identity = Identity(bytes);
        ResponseLossHttpClientFactory responseLossFactory = new(key);
        await using ServiceProvider provider = CreateResponseLossProvider(responseLossFactory);
        IConditionalObjectStorage adapter = provider.GetRequiredService<IConditionalObjectStorage>();

        ConditionalObjectWriteResult attempted = await adapter.TryCreateAsync(key, new MemoryStream(bytes), bytes.Length,
            "application/octet-stream", identity);
        attempted.Disposition.ShouldBe(ConditionalObjectWriteDisposition.Unavailable);
        responseLossFactory.DroppedSuccessfulResponse.ShouldBeTrue();

        ConditionalObjectStat recovered = await adapter.StatAsync(key);
        recovered.State.ShouldBe(ConditionalObjectState.Provisional);
        recovered.Length.ShouldBe(bytes.LongLength);
        AssertIdentity(recovered.Identity, identity);
        byte[] retryBytes = Encoding.UTF8.GetBytes("retry must not replace confirmed bytes");
        ConditionalObjectWriteResult retry = await adapter.TryCreateAsync(key, new MemoryStream(retryBytes), retryBytes.Length,
            "application/octet-stream", Identity(retryBytes));
        retry.Disposition.ShouldBe(ConditionalObjectWriteDisposition.Conflict);
        await using Stream final = await provider.GetRequiredService<IObjectStorage>().GetAsync(key);
        using MemoryStream finalBytes = new();
        await final.CopyToAsync(finalBytes);
        finalBytes.ToArray().ShouldBe(bytes);
    }

    [TestMethod]
    public async Task LostSuccessfulFenceResponseIsRecoveredByIndependentStatAndRetryWithoutAnotherPut()
    {
        string key = Key();
        byte[] bytes = Encoding.UTF8.GetBytes("provisional bytes before a lost fence response");
        ObjectWriteIdentity identity = Identity(bytes);
        (await _conditional.TryCreateAsync(key, new MemoryStream(bytes), bytes.Length, "application/octet-stream", identity))
            .Disposition.ShouldBe(ConditionalObjectWriteDisposition.Written);
        ObjectStorageRevision provisionalRevision = (await _conditional.StatAsync(key)).Revision!;

        ResponseLossHttpClientFactory responseLossFactory = new(key);
        await using ServiceProvider lossProvider = CreateResponseLossProvider(responseLossFactory);
        IConditionalObjectStorage lossAdapter = lossProvider.GetRequiredService<IConditionalObjectStorage>();
        ConditionalObjectWriteResult fence = await lossAdapter.TryReplaceWithCancellationAsync(key, provisionalRevision, identity);

        fence.Disposition.ShouldBe(ConditionalObjectWriteDisposition.Unavailable);
        responseLossFactory.DroppedSuccessfulResponse.ShouldBeTrue();
        responseLossFactory.PutRequests.ShouldBe(1);

        ConditionalObjectStat recovered = await _conditional.StatAsync(key);
        recovered.State.ShouldBe(ConditionalObjectState.Cancellation);
        recovered.Length.ShouldBe(0);
        AssertIdentity(recovered.Identity, identity);
        (await lossAdapter.TryReplaceWithCancellationAsync(key, provisionalRevision, identity)).Disposition
            .ShouldBe(ConditionalObjectWriteDisposition.AlreadyExists);
        responseLossFactory.PutRequests.ShouldBe(1);

        await using Stream sentinel = await _raw.GetAsync(key);
        using MemoryStream actual = new();
        await sentinel.CopyToAsync(actual);
        actual.ToArray().ShouldBe(Array.Empty<byte>());
    }

    [TestMethod]
    public async Task PartialNonSeekablePayloadFailureReturnsUnavailableAndLeavesObjectMissing()
    {
        string key = Key();
        byte[] completePayload = Encoding.UTF8.GetBytes("complete payload");
        ObjectWriteIdentity identity = Identity(completePayload);
        using ThrowAfterOneByteStream failingContent = new(completePayload);

        ConditionalObjectWriteResult result = await _conditional.TryCreateAsync(key, failingContent, completePayload.LongLength,
            "application/octet-stream", identity);

        result.Disposition.ShouldBe(ConditionalObjectWriteDisposition.Unavailable);
        (await _conditional.StatAsync(key)).State.ShouldBe(ConditionalObjectState.Missing);
        AmazonS3Exception missing = await Should.ThrowAsync<AmazonS3Exception>(() => _client.GetObjectAsync(_bucket, key));
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static ServiceProvider CreateResponseLossProvider(ResponseLossHttpClientFactory responseLossFactory)
    {
        IConfiguration configuration = CreateConfiguration(_serviceUrl, _bucket);
        ServiceCollection services = new();
        services.AddSingleton(configuration);
        services.AddStorageModule(configuration);
        services.RemoveAll<IAmazonS3>();
        services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(new BasicAWSCredentials(AccessKey, SecretKey),
            CreateS3Config(_serviceUrl, responseLossFactory)));
        return services.BuildServiceProvider();
    }

    private static IConfiguration CreateConfiguration(string serviceUrl, string bucket, string accessKey = AccessKey,
        string secretKey = SecretKey) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:ServiceUrl"] = serviceUrl,
            ["Storage:AccessKey"] = accessKey,
            ["Storage:SecretKey"] = secretKey,
            ["Storage:Bucket"] = bucket,
            ["Storage:Region"] = "us-east-1",
            ["Storage:CreateBucket"] = "false"
        }).Build();

    private static ServiceProvider CreateProvider(IConfiguration configuration)
    {
        ServiceCollection services = new();
        services.AddSingleton(configuration);
        services.AddStorageModule(configuration);
        return services.BuildServiceProvider();
    }

    private static AmazonS3Config CreateS3Config(string serviceUrl, HttpClientFactory? factory = null) => new()
    {
        ServiceURL = serviceUrl,
        AuthenticationRegion = "us-east-1",
        ForcePathStyle = true,
        MaxErrorRetry = 0,
        HttpClientFactory = factory
    };

    private static string Key() => $"_module-uploads/v1/orgs/{Guid.NewGuid():N}/objects/{Guid.NewGuid():N}";
    private static string Fingerprint() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("request metadata")));
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static ObjectWriteIdentity Identity(byte[] bytes) => new(Guid.NewGuid(), bytes.LongLength, Hash(bytes), Fingerprint());

    private static void AssertIdentity(ObjectWriteIdentity? actual, ObjectWriteIdentity expected)
    {
        actual.ShouldNotBeNull();
        actual!.OperationId.ShouldBe(expected.OperationId);
        actual.OriginalLength.ShouldBe(expected.OriginalLength);
        actual.Sha256.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
        actual.MetadataFingerprint.Equals(expected.MetadataFingerprint, StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
    }

    private sealed class GateStream(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content);
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
            return await _inner.ReadAsync(buffer, cancellationToken);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class ThrowAfterOneByteStream(byte[] content) : Stream
    {
        private int _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty) return 0;
            if (_read > 0) throw new IOException("The source stream failed after one byte.");
            _read++;
            buffer[0] = content[0];
            return 1;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.IsEmpty) return ValueTask.FromResult(0);
            if (_read > 0) return ValueTask.FromException<int>(new IOException("The source stream failed after one byte."));
            _read++;
            buffer.Span[0] = content[0];
            return ValueTask.FromResult(1);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ResponseLossHttpClientFactory(string key) : HttpClientFactory
    {
        private int _dropped;
        private int _putRequests;
        public bool DroppedSuccessfulResponse => Volatile.Read(ref _dropped) == 1;
        public int PutRequests => Volatile.Read(ref _putRequests);
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) =>
            new(new SuccessfulPutResponseDiscardHandler(key, () => Interlocked.Exchange(ref _dropped, 1),
                () => Interlocked.Increment(ref _putRequests)));
    }

    private sealed class SuccessfulPutResponseDiscardHandler(string key, Action responseDropped, Action putAttempted)
        : DelegatingHandler(new HttpClientHandler())
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Put
                && request.RequestUri?.AbsolutePath.EndsWith($"/{key}", StringComparison.Ordinal) == true)
            {
                putAttempted();
            }
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            if (request.Method == HttpMethod.Put
                && request.RequestUri?.AbsolutePath.EndsWith($"/{key}", StringComparison.Ordinal) == true
                && response.IsSuccessStatusCode)
            {
                response.Dispose();
                responseDropped();
                throw new HttpRequestException("The successful storage response was discarded by the test transport.");
            }

            return response;
        }
    }
}
#endif
