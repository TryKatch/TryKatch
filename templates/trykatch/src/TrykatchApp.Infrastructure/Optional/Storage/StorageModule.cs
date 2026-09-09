using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TrykatchApp.Infrastructure.Modules.Storage;

public interface IObjectStorage
{
    Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default);
    Task<Stream> GetAsync(string key, CancellationToken cancellationToken = default);
}

internal sealed class S3ObjectStorage(IAmazonS3 client, IConfiguration configuration) : IObjectStorage
{
    private string Bucket => configuration["Storage:Bucket"] ?? throw new InvalidOperationException("Storage:Bucket is required.");

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default) =>
        await client.PutObjectAsync(new PutObjectRequest { BucketName = Bucket, Key = key, InputStream = content, ContentType = contentType }, cancellationToken);

    public async Task<Stream> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        GetObjectResponse response = await client.GetObjectAsync(Bucket, key, cancellationToken);
        return response.ResponseStream;
    }
}

internal sealed class LocalObjectStorage(IConfiguration configuration) : IObjectStorage
{
    private string Root => Path.GetFullPath(configuration["Storage:LocalPath"] ?? Path.Combine(AppContext.BaseDirectory, "storage"));

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
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

    private string Resolve(string key)
    {
        string path = Path.GetFullPath(Path.Combine(Root, key.TrimStart('/', '\\')));
        if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Object key escapes the configured storage root.");
        }

        return path;
    }
}

public static class StorageModule
{
    public static IServiceCollection AddTrykatchStorage(this IServiceCollection services, IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration["Storage:ServiceUrl"]))
        {
            services.AddScoped<IObjectStorage, LocalObjectStorage>();
            return services;
        }

        services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(new AmazonS3Config
        {
            ServiceURL = configuration["Storage:ServiceUrl"],
            ForcePathStyle = true
        }));
        services.AddScoped<IObjectStorage, S3ObjectStorage>();
        return services;
    }
}
