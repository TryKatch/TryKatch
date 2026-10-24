#if TRYKATCH_STORAGE
using System.Net;
using System.Net.Http;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Shouldly;

namespace Trykatch.IntegrationTests;

[TestClass]
public sealed class ObjectStorageConditionalWriteIntegrationTests
{
    private const string AccessKey = "trykatch-object-storage-test";
    private const string SecretKey = "trykatch-object-storage-test-secret";
    private const string Image =
        "docker.io/rustfs/rustfs:1.0.3@sha256:9ca9559533b537d10482fe923288b676ad08f92a7069d9a17327fbae3cab688b";

    private StorageFixture _fixture = null!;

    [TestInitialize]
    public async Task StartStorage()
    {
        _fixture = await StorageFixture.StartAsync();
    }

    [TestCleanup]
    public async Task StopStorage()
    {
        if (_fixture is not null)
        {
            await _fixture.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ConcurrentCreateIfAbsentAllowsOneWriterAndKeepsItsBytesAndMetadata()
    {
        const string key = "immutable/concurrent-create";
        const int writerCount = 8;
        WriteAttempt[] attempts = await Task.WhenAll(Enumerable.Range(0, writerCount).Select(async index =>
        {
            string operationId = $"operation-{index}";
            byte[] bytes = Encoding.UTF8.GetBytes($"payload-{index}");
            try
            {
                PutObjectResponse response = await _fixture.Client.PutObjectAsync(
                    CreatePutRequest(key, bytes, operationId, ifNoneMatch: "*"));
                return new WriteAttempt(operationId, bytes, response.ETag, null);
            }
            catch (AmazonS3Exception exception)
            {
                return new WriteAttempt(operationId, bytes, null, exception);
            }
        }));

        WriteAttempt[] winners = attempts.Where(attempt => attempt.Exception is null).ToArray();
        winners.Length.ShouldBe(1);
        attempts.Where(attempt => attempt.Exception is not null)
            .Select(attempt => attempt.Exception!.StatusCode)
            .ShouldAllBe(status => status == HttpStatusCode.PreconditionFailed);

        (byte[] bytes, string operationId) = await _fixture.ReadAsync(key);
        bytes.ShouldBe(winners[0].Bytes);
        operationId.ShouldBe(winners[0].OperationId);
    }

    [TestMethod]
    public async Task CancellationSentinelRejectsDelayedAndDuplicateInitialWritesAfterRestart()
    {
        const string key = "immutable/cancelled-upload";
        const string sentinelOperationId = "cancelled-operation";
        await _fixture.Client.PutObjectAsync(CreatePutRequest(key, [], sentinelOperationId, ifNoneMatch: "*"));

        await _fixture.RestartAsync();

        foreach (string delayedOperationId in new[] { "delayed-initial-write", "duplicate-initial-write" })
        {
            AmazonS3Exception rejected = await Should.ThrowAsync<AmazonS3Exception>(() => _fixture.Client.PutObjectAsync(
                CreatePutRequest(key, Encoding.UTF8.GetBytes(delayedOperationId), delayedOperationId, ifNoneMatch: "*")));
            rejected.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        }

        (byte[] bytes, string operationId) = await _fixture.ReadAsync(key);
        bytes.ShouldBeEmpty();
        operationId.ShouldBe(sentinelOperationId);
    }

    [TestMethod]
    public async Task CurrentEtagCanReplaceProvisionalObjectButStaleIdentityCannotReplaceSentinel()
    {
        const string key = "immutable/provisional-object";
        const string provisionalOperationId = "provisional-operation";
        const string sentinelOperationId = "cancelled-operation";
        byte[] provisionalBytes = Encoding.UTF8.GetBytes("provisional bytes");
        byte[] sentinelBytes = [];
        PutObjectResponse provisional = await _fixture.Client.PutObjectAsync(
            CreatePutRequest(key, provisionalBytes, provisionalOperationId, ifNoneMatch: "*"));

        AmazonS3Exception staleReplacement = await Should.ThrowAsync<AmazonS3Exception>(() => _fixture.Client.PutObjectAsync(
            CreatePutRequest(key, sentinelBytes, sentinelOperationId, ifMatch: "\"stale-identity\"")));
        staleReplacement.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (byte[] existingBytes, string existingOperationId) = await _fixture.ReadAsync(key);
        existingBytes.ShouldBe(provisionalBytes);
        existingOperationId.ShouldBe(provisionalOperationId);

        await _fixture.Client.PutObjectAsync(CreatePutRequest(key, sentinelBytes, sentinelOperationId, ifMatch: provisional.ETag));

        AmazonS3Exception staleSentinelReplacement = await Should.ThrowAsync<AmazonS3Exception>(() => _fixture.Client.PutObjectAsync(
            CreatePutRequest(key, provisionalBytes, "stale-retry", ifMatch: provisional.ETag)));
        staleSentinelReplacement.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        AmazonS3Exception initialRetry = await Should.ThrowAsync<AmazonS3Exception>(() => _fixture.Client.PutObjectAsync(
            CreatePutRequest(key, provisionalBytes, "initial-retry", ifNoneMatch: "*")));
        initialRetry.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);

        (byte[] finalBytes, string finalOperationId) = await _fixture.ReadAsync(key);
        finalBytes.ShouldBeEmpty();
        finalOperationId.ShouldBe(sentinelOperationId);
    }

    [TestMethod]
    public async Task LostSuccessfulCreateResponseIsRecoveredByReadAndConditionalRetry()
    {
        const string key = "immutable/lost-response";
        const string operationId = "committed-before-response-loss";
        byte[] bytes = Encoding.UTF8.GetBytes("server committed these bytes");
        ResponseLossHttpClientFactory responseLossFactory = new(key);
        using AmazonS3Client responseLossClient = _fixture.CreateClient(responseLossFactory);

        Exception transportFailure = await Should.ThrowAsync<Exception>(() => responseLossClient.PutObjectAsync(
            CreatePutRequest(key, bytes, operationId, ifNoneMatch: "*")));
        responseLossFactory.DroppedSuccessfulResponse.ShouldBeTrue(transportFailure.ToString());

        (byte[] recoveredBytes, string recoveredOperationId) = await _fixture.ReadAsync(key);
        recoveredBytes.ShouldBe(bytes);
        recoveredOperationId.ShouldBe(operationId);

        AmazonS3Exception retryRejected = await Should.ThrowAsync<AmazonS3Exception>(() => _fixture.Client.PutObjectAsync(
            CreatePutRequest(key, Encoding.UTF8.GetBytes("retry must not replace"), "retry", ifNoneMatch: "*")));
        retryRejected.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);

        (byte[] finalBytes, string finalOperationId) = await _fixture.ReadAsync(key);
        finalBytes.ShouldBe(bytes);
        finalOperationId.ShouldBe(operationId);
    }

    [TestMethod]
    public async Task EnabledBucketVersioningRetainsProvisionalBytesAfterSentinelReplacement()
    {
        GetBucketVersioningResponse freshVersioning = await _fixture.Client.GetBucketVersioningAsync(_fixture.Bucket);
        (freshVersioning.VersioningConfig.Status is null || freshVersioning.VersioningConfig.Status == VersionStatus.Off)
            .ShouldBeTrue("a new technical bucket must not already be versioned");

        await _fixture.Client.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = _fixture.Bucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled }
        });
        GetBucketVersioningResponse enabledVersioning = await _fixture.Client.GetBucketVersioningAsync(_fixture.Bucket);
        enabledVersioning.VersioningConfig.Status.ShouldBe(VersionStatus.Enabled);

        const string key = "immutable/versioned-provisional";
        const string provisionalOperationId = "versioned-provisional-operation";
        byte[] provisionalBytes = Encoding.UTF8.GetBytes("versioned provisional bytes");
        PutObjectResponse provisional = await _fixture.Client.PutObjectAsync(
            CreatePutRequest(key, provisionalBytes, provisionalOperationId, ifNoneMatch: "*"));
        await _fixture.Client.PutObjectAsync(
            CreatePutRequest(key, [], "versioned-cancelled-operation", ifMatch: provisional.ETag));

        ListVersionsResponse versions = await _fixture.Client.ListVersionsAsync(new ListVersionsRequest
        {
            BucketName = _fixture.Bucket,
            Prefix = key
        });
        S3ObjectVersion provisionalVersion = versions.Versions.Single(version =>
            version.Key == key && version.VersionId == provisional.VersionId);
        provisionalVersion.IsLatest.ShouldBe(false);

        using GetObjectResponse retained = await _fixture.Client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = _fixture.Bucket,
            Key = key,
            VersionId = provisional.VersionId
        });
        using MemoryStream retainedBytes = new();
        await retained.ResponseStream.CopyToAsync(retainedBytes);
        retainedBytes.ToArray().ShouldBe(provisionalBytes);
        retained.Metadata["x-amz-meta-operation-id"].ShouldBe(provisionalOperationId);

        (byte[] currentBytes, string currentOperationId) = await _fixture.ReadAsync(key);
        currentBytes.ShouldBeEmpty();
        currentOperationId.ShouldBe("versioned-cancelled-operation");
    }

    private PutObjectRequest CreatePutRequest(
        string key,
        byte[] bytes,
        string operationId,
        string? ifNoneMatch = null,
        string? ifMatch = null)
    {
        PutObjectRequest request = new()
        {
            BucketName = _fixture.Bucket,
            Key = key,
            InputStream = new MemoryStream(bytes),
            IfNoneMatch = ifNoneMatch,
            IfMatch = ifMatch
        };
        request.Metadata.Add("operation-id", operationId);
        return request;
    }

    private sealed record WriteAttempt(string OperationId, byte[] Bytes, string? ETag, AmazonS3Exception? Exception);

    private sealed class StorageFixture : IAsyncDisposable
    {
        private IContainer _container;

        private StorageFixture(IContainer container, string bucket)
        {
            _container = container;
            Bucket = bucket;
            Client = CreateClient();
        }

        public string Bucket { get; }

        public AmazonS3Client Client { get; private set; }

        public static async Task<StorageFixture> StartAsync()
        {
            IContainer container = new ContainerBuilder(Image)
                .WithEnvironment("RUSTFS_ACCESS_KEY", AccessKey)
                .WithEnvironment("RUSTFS_SECRET_KEY", SecretKey)
                .WithEnvironment("RUSTFS_CONSOLE_ENABLE", "false")
                .WithEnvironment("RUSTFS_ADDRESS", ":9000")
                .WithPortBinding(9000, assignRandomHostPort: true)
                .WithCommand("/data")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request =>
                    request.ForPort(9000).ForPath("/health/ready")))
                .Build();
            StorageFixture? fixture = null;
            try
            {
                await container.StartAsync();
                fixture = new StorageFixture(container, $"conditional-{Guid.NewGuid():N}");
                await fixture.Client.PutBucketAsync(fixture.Bucket);
                return fixture;
            }
            catch
            {
                if (fixture is not null)
                {
                    await fixture.DisposeAsync();
                }
                else
                {
                    await container.DisposeAsync();
                }

                throw;
            }
        }

        public async Task RestartAsync()
        {
            Client.Dispose();
            await _container.StopAsync();
            await _container.StartAsync();
            Client = CreateClient();
        }

        public AmazonS3Client CreateClient(HttpClientFactory? httpClientFactory = null)
        {
            int port = _container.GetMappedPublicPort(9000);
            AmazonS3Config config = new()
            {
                ServiceURL = $"http://127.0.0.1:{port}",
                AuthenticationRegion = "us-east-1",
                ForcePathStyle = true,
                MaxErrorRetry = 0,
                HttpClientFactory = httpClientFactory
            };
            return new AmazonS3Client(new BasicAWSCredentials(AccessKey, SecretKey), config);
        }

        public async Task<(byte[] Bytes, string OperationId)> ReadAsync(string key)
        {
            using GetObjectResponse response = await Client.GetObjectAsync(Bucket, key);
            using MemoryStream bytes = new();
            await response.ResponseStream.CopyToAsync(bytes);
            return (bytes.ToArray(), response.Metadata["x-amz-meta-operation-id"]);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _container.DisposeAsync();
        }

        private AmazonS3Client CreateClient() => CreateClient(null);
    }

    private sealed class ResponseLossHttpClientFactory(string key) : HttpClientFactory
    {
        private int _droppedSuccessfulResponse;

        public bool DroppedSuccessfulResponse => Volatile.Read(ref _droppedSuccessfulResponse) == 1;

        public override HttpClient CreateHttpClient(IClientConfig clientConfig) =>
            new(new SuccessfulPutResponseDiscardHandler(key, () => Interlocked.Exchange(ref _droppedSuccessfulResponse, 1)));
    }

    private sealed class SuccessfulPutResponseDiscardHandler(string key, Action responseDropped) : DelegatingHandler(new HttpClientHandler())
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
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
