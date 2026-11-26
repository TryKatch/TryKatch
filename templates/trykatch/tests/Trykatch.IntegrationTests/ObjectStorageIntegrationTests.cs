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
        "rustfs/rustfs:1.0.7@sha256:69bd3c4d8d79b74723bfd68c9fbd9085155d4d4b44a5a136d87502f3075cc68c";

    [TestMethod]
    public async Task S3CompatibleAdapterStoresAndReadsAnObject()
    {
        await using IContainer objectStore = new ContainerBuilder(Image)
            .WithEnvironment("RUSTFS_ACCESS_KEY", "trykatch-test")
            .WithEnvironment("RUSTFS_SECRET_KEY", "trykatch-test-secret")
            .WithPortBinding(9000, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request =>
                request.ForPort(9000).ForPath("/health/ready")))
            .Build();
        await objectStore.StartAsync();

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:ServiceUrl"] = $"http://127.0.0.1:{objectStore.GetMappedPublicPort(9000)}",
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
