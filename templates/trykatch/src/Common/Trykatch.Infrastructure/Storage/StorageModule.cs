using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Trykatch.Modules;
#if TRYKATCH_STORAGE
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
#endif

namespace Trykatch.Infrastructure.Storage;

#if TRYKATCH_STORAGE
internal sealed class S3ObjectStorage(IAmazonS3 client, IConfiguration configuration) : IObjectStorage, IDisposable
{
    private readonly SemaphoreSlim initializationLock = new(1, 1);
    private bool bucketReady;

    private string Bucket => configuration["Storage:Bucket"]
        ?? throw new InvalidOperationException("Storage:Bucket is required.");

    public async Task PutAsync(
        string key,
        Stream content,
        long contentLength,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        await EnsureBucketAsync(cancellationToken);
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = NormalizeKey(key),
            InputStream = content,
            ContentType = contentType,
            AutoCloseStream = false,
            Headers = { ContentLength = contentLength }
        }, cancellationToken);
    }

    public async Task<Stream> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        await EnsureBucketAsync(cancellationToken);
        GetObjectResponse response = await client.GetObjectAsync(Bucket, NormalizeKey(key), cancellationToken);
        return response.ResponseStream;
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        await EnsureBucketAsync(cancellationToken);
        await client.DeleteObjectAsync(Bucket, NormalizeKey(key), cancellationToken);
    }

    private async Task EnsureBucketAsync(CancellationToken cancellationToken)
    {
        if (bucketReady) return;
        await initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (bucketReady) return;
            bool exists = await AmazonS3Util.DoesS3BucketExistV2Async(client, Bucket);
            if (!exists)
            {
                bool createBucket = string.Equals(
                    configuration["Storage:CreateBucket"],
                    "true",
                    StringComparison.OrdinalIgnoreCase);
                if (!createBucket)
                    throw new InvalidOperationException($"Object-storage bucket '{Bucket}' does not exist.");
                await client.PutBucketAsync(Bucket, cancellationToken);
            }
            bucketReady = true;
        }
        finally
        {
            initializationLock.Release();
        }
    }

    private static string NormalizeKey(string key)
    {
        string normalized = key.Trim().TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("Object key is invalid.");
        return normalized;
    }

    public void Dispose() => initializationLock.Dispose();
}
#endif

internal sealed class LocalObjectStorage(IConfiguration configuration) : IObjectStorage
{
    private string Root => Path.GetFullPath(
        configuration["Storage:LocalPath"]
        ?? Path.Combine(AppContext.BaseDirectory, "storage"));

    public async Task PutAsync(
        string key,
        Stream content,
        long contentLength,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        _ = contentLength;
        _ = contentType;
        string path = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using FileStream destination = File.Create(path);
        await content.CopyToAsync(destination, cancellationToken);
    }

    public Task<Stream> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<Stream>(File.OpenRead(Resolve(key)));
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = Resolve(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string Resolve(string key)
    {
        string path = Path.GetFullPath(Path.Combine(Root, key.TrimStart('/', '\\')));
        if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Object key escapes the configured storage root.");
        return path;
    }
}

public static class StorageModule
{
    public static IServiceCollection AddStorageModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
#if TRYKATCH_STORAGE
        string? serviceUrl = configuration["Storage:ServiceUrl"];
        if (!string.IsNullOrWhiteSpace(serviceUrl))
        {
            string accessKey = configuration["Storage:AccessKey"]
                ?? throw new InvalidOperationException("Storage:AccessKey is required for S3-compatible storage.");
            string secretKey = configuration["Storage:SecretKey"]
                ?? throw new InvalidOperationException("Storage:SecretKey is required for S3-compatible storage.");
            services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(
                new BasicAWSCredentials(accessKey, secretKey),
                new AmazonS3Config
                {
                    ServiceURL = serviceUrl,
                    AuthenticationRegion = configuration["Storage:Region"] ?? "us-east-1",
                    ForcePathStyle = true
                }));
            services.AddScoped<IObjectStorage, S3ObjectStorage>();
            return services;
        }
#endif
        services.AddScoped<IObjectStorage, LocalObjectStorage>();
        return services;
    }
}
