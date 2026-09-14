#if TRYKATCH_STORAGE
using System.Text;
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
    private const string Image =
        "quay.io/minio/minio:RELEASE.2025-09-07T16-13-09Z@sha256:14cea493d9a34af32f524e538b8346cf79f3321eff8e708c1e2960462bd8936e";

    [TestMethod]
    public async Task S3CompatibleAdapterStoresAndReadsAnObjectFromMinio()
    {
        await using IContainer minio = new ContainerBuilder(Image)
            .WithEnvironment("MINIO_ROOT_USER", "trykatch-test")
            .WithEnvironment("MINIO_ROOT_PASSWORD", "trykatch-test-secret")
            .WithPortBinding(9000, assignRandomHostPort: true)
            .WithCommand("server", "/data")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request =>
                request.ForPort(9000).ForPath("/minio/health/ready")))
            .Build();
        await minio.StartAsync();

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:ServiceUrl"] = $"http://127.0.0.1:{minio.GetMappedPublicPort(9000)}",
                ["Storage:AccessKey"] = "trykatch-test",
                ["Storage:SecretKey"] = "trykatch-test-secret",
                ["Storage:Bucket"] = "documents-test",
                ["Storage:Region"] = "us-east-1",
                ["Storage:CreateBucket"] = "true"
            })
            .Build();
        ServiceCollection services = new();
        services.AddSingleton(configuration);
        services.AddStorageModule(configuration);
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IObjectStorage storage = scope.ServiceProvider.GetRequiredService<IObjectStorage>();
        await using AsyncServiceScope secondScope = provider.CreateAsyncScope();
        secondScope.ServiceProvider.GetRequiredService<IObjectStorage>().ShouldBeSameAs(storage);

        const string expected = "private organization document";
        await using MemoryStream upload = new(Encoding.UTF8.GetBytes(expected));
        await storage.PutAsync(
            "organizations/11111111-1111-1111-1111-111111111111/documents/test",
            upload,
            upload.Length,
            "text/plain");

        await using Stream downloaded = await storage.GetAsync(
            "organizations/11111111-1111-1111-1111-111111111111/documents/test");
        using StreamReader reader = new(downloaded, Encoding.UTF8);
        (await reader.ReadToEndAsync()).ShouldBe(expected);
    }
}
#endif
