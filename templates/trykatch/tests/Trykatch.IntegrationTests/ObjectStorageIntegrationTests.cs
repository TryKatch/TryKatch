#if TRYKATCH_STORAGE
using System.Net;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trykatch.Infrastructure.Storage;
using Trykatch.Modules;

namespace Trykatch.IntegrationTests;

[TestClass]
public sealed class ObjectStorageIntegrationTests
{
    private const string AccessKey = "trykatch-object-storage-test";
    private const string SecretKey = "trykatch-object-storage-test-secret";
    private const string Image =
        "docker.io/rustfs/rustfs:1.0.3@sha256:9ca9559533b537d10482fe923288b676ad08f92a7069d9a17327fbae3cab688b";

    [TestMethod]
    public async Task S3AdapterStoresReadsDeletesAndRejectsUnauthorizedRequests()
    {
        await using IContainer objectStorage = new ContainerBuilder(Image)
            .WithEnvironment("RUSTFS_ACCESS_KEY", AccessKey)
            .WithEnvironment("RUSTFS_SECRET_KEY", SecretKey)
            .WithEnvironment("RUSTFS_CONSOLE_ENABLE", "false")
            .WithEnvironment("RUSTFS_ADDRESS", ":9000")
            .WithPortBinding(9000, assignRandomHostPort: true)
            .WithCommand("/data")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request =>
                request.ForPort(9000).ForPath("/health/ready")))
            .Build();
        await objectStorage.StartAsync();

        int endpointPort = objectStorage.GetMappedPublicPort(9000);
        string serviceUrl = $"http://127.0.0.1:{endpointPort}";
        string bucket = $"documents-{Guid.NewGuid():N}";
        const string key = "organizations/11111111-1111-1111-1111-111111111111/documents/test";
        byte[] expected = Encoding.UTF8.GetBytes("private organization document");
        await using (ServiceProvider provider = CreateStorageProvider(serviceUrl, bucket))
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            IObjectStorage storage = scope.ServiceProvider.GetRequiredService<IObjectStorage>();
            await using AsyncServiceScope secondScope = provider.CreateAsyncScope();
            secondScope.ServiceProvider.GetRequiredService<IObjectStorage>().ShouldBeSameAs(storage);

            await using MemoryStream upload = new(expected);
            await storage.PutAsync(key, upload, upload.Length, "text/plain");
        }

        await objectStorage.StopAsync();
        await objectStorage.StartAsync();

        string restartedServiceUrl = $"http://127.0.0.1:{objectStorage.GetMappedPublicPort(9000)}";
        await using (ServiceProvider provider = CreateStorageProvider(restartedServiceUrl, bucket))
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            IObjectStorage storage = scope.ServiceProvider.GetRequiredService<IObjectStorage>();
            await using (Stream downloaded = await storage.GetAsync(key))
            using (MemoryStream actual = new())
            {
                await downloaded.CopyToAsync(actual);
                actual.ToArray().ShouldBe(expected);
            }

            AmazonS3Config s3Config = CreateS3Config(restartedServiceUrl);
            using (AmazonS3Client wrongCredentials = new(
                new BasicAWSCredentials("wrong-object-storage-key", "wrong-object-storage-secret"),
                s3Config))
            {
                AmazonS3Exception denied = await Should.ThrowAsync<AmazonS3Exception>(
                    () => wrongCredentials.GetObjectAsync(bucket, key));
                denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
                denied.ErrorCode.ShouldBe("InvalidAccessKeyId");
            }

            using (AmazonS3Client anonymous = new(new AnonymousAWSCredentials(), s3Config))
            {
                AmazonS3Exception denied = await Should.ThrowAsync<AmazonS3Exception>(
                    () => anonymous.GetObjectAsync(bucket, key));
                denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
                denied.ErrorCode.ShouldBe("AccessDenied");
            }

            await storage.DeleteAsync(key);
            using AmazonS3Client authorized = new(new BasicAWSCredentials(AccessKey, SecretKey), s3Config);
            AmazonS3Exception missing = await Should.ThrowAsync<AmazonS3Exception>(
                () => authorized.GetObjectAsync(bucket, key));
            missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
    }

    private static ServiceProvider CreateStorageProvider(string serviceUrl, string bucket)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:ServiceUrl"] = serviceUrl,
                ["Storage:AccessKey"] = AccessKey,
                ["Storage:SecretKey"] = SecretKey,
                ["Storage:Bucket"] = bucket,
                ["Storage:Region"] = "us-east-1",
                ["Storage:CreateBucket"] = "true"
            })
            .Build();
        ServiceCollection services = new();
        services.AddSingleton(configuration);
        services.AddStorageModule(configuration);
        return services.BuildServiceProvider();
    }

    private static AmazonS3Config CreateS3Config(string serviceUrl) => new()
    {
        ServiceURL = serviceUrl,
        AuthenticationRegion = "us-east-1",
        ForcePathStyle = true
    };
}
#endif
