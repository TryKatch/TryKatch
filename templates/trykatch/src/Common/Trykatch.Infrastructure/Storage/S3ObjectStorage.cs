#if TRYKATCH_STORAGE
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Microsoft.Extensions.Configuration;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Storage;

internal sealed class S3ObjectStorage(IAmazonS3 client, IConfiguration configuration)
    : IObjectStorage, IConditionalObjectStorage, IDisposable
{
    private readonly SemaphoreSlim initializationLock = new(1, 1);
    private readonly string bucket = configuration["Storage:Bucket"]
        ?? throw new InvalidOperationException("Storage:Bucket is required.");
    private readonly string endpoint = CanonicalEndpoint(client.Config.ServiceURL);
    private readonly bool createBucket = string.Equals(configuration["Storage:CreateBucket"], "true", StringComparison.OrdinalIgnoreCase);
    private bool bucketReady;

    public string NamespaceFingerprint => ConditionalObjectPolicy.NamespaceFingerprint("s3-v1", endpoint, bucket);

    public async Task<ConditionalObjectWriteResult> TryCreateAsync(string key, Stream content, long length,
        string contentType, ObjectWriteIdentity identity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ConditionalObjectPolicy.IsValidKey(key) || !ConditionalObjectPolicy.TryIdentity(identity, out ObjectWriteIdentity normalized)
            || length != normalized.OriginalLength || !ConditionalObjectPolicy.IsValidContentType(contentType))
            return new(ConditionalObjectWriteDisposition.InvalidPayload);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConditionalObjectPolicy.OperationTimeout);
        try
        {
            await using VerifiedObjectPayload payload = await VerifiedObjectPayload.CreateAsync(content, length, normalized, timeout.Token);
            await EnsureBucketAsync(timeout.Token);
            PutObjectRequest request = Request(key, payload.Stream, length, contentType, normalized, cancellation: false, expected: null);
            try
            {
                PutObjectResponse response = await client.PutObjectAsync(request, timeout.Token);
                return Acknowledged(response, normalized, cancellation: false);
            }
            catch (AmazonS3Exception failure) when (IsPreconditionLoss(failure))
            {
                ConditionalObjectStat current = await ReadStatAsync(key, timeout.Token);
                return current.State == ConditionalObjectState.Unavailable ? Unavailable() : ConditionalObjectPolicy.Existing(current, normalized);
            }
        }
        catch (InvalidDataException) { return new(ConditionalObjectWriteDisposition.InvalidPayload); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Unavailable(); }
        catch (Exception failure) when (IsStorageFailure(failure)) { return Unavailable(); }
    }

    public async Task<ConditionalObjectStat> StatAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ConditionalObjectPolicy.IsValidKey(key)) return new(ConditionalObjectState.Unowned);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConditionalObjectPolicy.OperationTimeout);
        try
        {
            await EnsureBucketAsync(timeout.Token);
            return await ReadStatAsync(key, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(ConditionalObjectState.Unavailable); }
        catch (Exception failure) when (IsStorageFailure(failure)) { return new(ConditionalObjectState.Unavailable); }
    }

    public async Task<ConditionalObjectWriteResult> TryReplaceWithCancellationAsync(string key, ObjectStorageRevision? expectedVersion,
        ObjectWriteIdentity identity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ConditionalObjectPolicy.IsValidKey(key) || !ConditionalObjectPolicy.TryIdentity(identity, out ObjectWriteIdentity normalized)
            || !ConditionalObjectPolicy.IsValidRevision(expectedVersion)) return new(ConditionalObjectWriteDisposition.InvalidPayload);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConditionalObjectPolicy.OperationTimeout);
        try
        {
            await EnsureBucketAsync(timeout.Token);
            ConditionalObjectStat current = await ReadStatAsync(key, timeout.Token);
            if (current.State == ConditionalObjectState.Cancellation) return FenceConflict(current, normalized);
            bool missing = current.State == ConditionalObjectState.Missing && expectedVersion is null;
            bool owned = current.State == ConditionalObjectState.Provisional && expectedVersion is not null
                && ConditionalObjectPolicy.SameIdentity(current.Identity, normalized) && current.Revision == expectedVersion;
            if (!missing && !owned) return FenceConflict(current, normalized);
            using MemoryStream empty = new();
            PutObjectRequest request = Request(key, empty, 0, "application/octet-stream", normalized, cancellation: true, expectedVersion);
            try
            {
                PutObjectResponse response = await client.PutObjectAsync(request, timeout.Token);
                return Acknowledged(response, normalized, cancellation: true);
            }
            catch (AmazonS3Exception failure) when (IsPreconditionLoss(failure) || failure.StatusCode == HttpStatusCode.NotFound)
            {
                return FenceConflict(await ReadStatAsync(key, timeout.Token), normalized);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Unavailable(); }
        catch (Exception failure) when (IsStorageFailure(failure)) { return Unavailable(); }
    }

    public async Task PutAsync(string key, Stream content, long contentLength, string contentType,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string normalized = NormalizeKey(key);
        RefuseManagedRawKey(normalized);
        await EnsureBucketAsync(cancellationToken);
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket, Key = normalized, InputStream = content, ContentType = contentType,
            AutoCloseStream = false, Headers = { ContentLength = contentLength }
        }, cancellationToken);
    }

    public async Task<Stream> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        await EnsureBucketAsync(cancellationToken);
        GetObjectResponse response = await client.GetObjectAsync(bucket, NormalizeKey(key), cancellationToken);
        return response.ResponseStream;
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string normalized = NormalizeKey(key);
        RefuseManagedRawKey(normalized);
        await EnsureBucketAsync(cancellationToken);
        await client.DeleteObjectAsync(bucket, normalized, cancellationToken);
    }

    private PutObjectRequest Request(string key, Stream content, long length, string contentType, ObjectWriteIdentity identity,
        bool cancellation, ObjectStorageRevision? expected)
    {
        PutObjectRequest request = new()
        {
            BucketName = bucket, Key = key, InputStream = content, ContentType = contentType, AutoCloseStream = false,
            Headers = { ContentLength = length }, IfNoneMatch = expected is null ? "*" : null, IfMatch = expected?.Value
        };
        S3ConditionalObjectMetadata.Write(request, identity, cancellation);
        return request;
    }

    private async Task<ConditionalObjectStat> ReadStatAsync(string key, CancellationToken token)
    {
        try
        {
            GetObjectMetadataResponse response = await client.GetObjectMetadataAsync(bucket, key, token);
            return S3ConditionalObjectMetadata.Read(response);
        }
        catch (AmazonS3Exception failure) when (failure.StatusCode == HttpStatusCode.NotFound)
        {
            return new(ConditionalObjectState.Missing);
        }
    }

    private async Task EnsureBucketAsync(CancellationToken cancellationToken)
    {
        if (CanonicalEndpoint(client.Config.ServiceURL) != endpoint)
            throw new InvalidOperationException("Storage destination changed after initialization.");
        if (bucketReady) return;
        await initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (bucketReady) return;
            if (createBucket && !await AmazonS3Util.DoesS3BucketExistV2Async(client, bucket))
                await client.PutBucketAsync(bucket, cancellationToken);
            bucketReady = true;
        }
        finally { initializationLock.Release(); }
    }

    private static ConditionalObjectWriteResult Acknowledged(PutObjectResponse response, ObjectWriteIdentity identity, bool cancellation)
    {
        if (!ConditionalObjectPolicy.IsValidRevision(new(response.ETag))) return Unavailable();
        return new(ConditionalObjectWriteDisposition.Written, new(
            cancellation ? ConditionalObjectState.Cancellation : ConditionalObjectState.Provisional,
            cancellation ? 0 : identity.OriginalLength, identity, new(response.ETag)));
    }

    private static ConditionalObjectWriteResult FenceConflict(ConditionalObjectStat current, ObjectWriteIdentity identity) =>
        current.State == ConditionalObjectState.Unavailable ? Unavailable()
            : new(current.State == ConditionalObjectState.Cancellation && ConditionalObjectPolicy.SameIdentity(current.Identity, identity)
                ? ConditionalObjectWriteDisposition.AlreadyExists : ConditionalObjectWriteDisposition.Conflict, current);

    private static bool IsPreconditionLoss(AmazonS3Exception failure) => failure.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict;
    private static bool IsStorageFailure(Exception failure) => failure is AmazonS3Exception or HttpRequestException or IOException
        or TimeoutException or InvalidOperationException;
    private static ConditionalObjectWriteResult Unavailable() => new(ConditionalObjectWriteDisposition.Unavailable);

    private static string NormalizeKey(string key)
    {
        string normalized = key.Trim().TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("Object key is invalid.");
        return normalized;
    }

    private static void RefuseManagedRawKey(string key)
    {
        if (ConditionalObjectPolicy.IsReservedRawKey(key)) throw new InvalidOperationException("The managed upload namespace is reserved.");
    }

    private static string CanonicalEndpoint(string? endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Storage destination is invalid.");
        return uri.AbsoluteUri.TrimEnd('/');
    }

    public void Dispose() => initializationLock.Dispose();
}
#endif
