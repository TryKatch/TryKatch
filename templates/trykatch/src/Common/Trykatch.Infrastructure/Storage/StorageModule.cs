using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Trykatch.Modules;
#if TRYKATCH_STORAGE
using Amazon.Runtime;
using Amazon.S3;
#endif

namespace Trykatch.Infrastructure.Storage;

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
            services.AddSingleton<S3ObjectStorage>();
            services.AddSingleton<IObjectStorage>(provider => provider.GetRequiredService<S3ObjectStorage>());
            services.AddSingleton<IConditionalObjectStorage>(provider => provider.GetRequiredService<S3ObjectStorage>());
            return services;
        }
#endif
        services.AddSingleton<LocalObjectStorage>();
        services.AddSingleton<IObjectStorage>(provider => provider.GetRequiredService<LocalObjectStorage>());
        services.AddSingleton<IConditionalObjectStorage>(provider => provider.GetRequiredService<LocalObjectStorage>());
        return services;
    }
}
