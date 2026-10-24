using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shouldly;
using Trykatch.Infrastructure.Caching;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class OrganizationCacheCompositionTests
{
    [TestMethod]
    public async Task CacheHealthDistinguishesOptionalModesAndSanitizesUnavailableBackend()
    {
        foreach (string mode in new[] { "Disabled", "LocalOnly", "Distributed" })
        {
            using ServiceProvider provider = Build(new Dictionary<string, string?>
            {
                ["Cache:Mode"] = mode,
                ["ConnectionStrings:cache"] = "127.0.0.1:1,password=private-health-credential"
            });
            HealthReport report = await provider.GetRequiredService<HealthCheckService>()
                .CheckHealthAsync(registration => registration.Name == "organization-cache");
            HealthReportEntry entry = report.Entries["organization-cache"];
            entry.Status.ShouldBe(mode == "Distributed" ? HealthStatus.Degraded : HealthStatus.Healthy);
            entry.Exception.ShouldBeNull();
            entry.Description!.ShouldNotContain("127.0.0.1");
            entry.Description!.ShouldNotContain("private-health-credential");
            entry.Data.ShouldBeEmpty();
        }
    }

    [TestMethod]
    public void BindingFailuresDoNotExposeRawConfigurationOrInnerExceptions()
    {
        const string rawValue = "private-invalid-configuration";
        foreach (string key in new[] { "Cache:Mode", "Cache:FactoryTimeout", "Cache:TtlSeconds:short" })
        {
            InvalidOperationException failure = Should.Throw<InvalidOperationException>(() => Build(
                new Dictionary<string, string?> { [key] = rawValue }));
            failure.Message.ShouldNotContain(rawValue);
            failure.InnerException.ShouldBeNull();
        }
    }

    [TestMethod]
    public void DefaultAndGenerationCompositionResolveWithoutNetworkOrCacheCredentials()
    {
        using ServiceProvider disabled = Build(new Dictionary<string, string?>());
        disabled.GetRequiredService<IOptions<OrganizationCacheOptions>>().Value.Mode.ShouldBe(OrganizationCacheMode.Disabled);
        disabled.GetRequiredKeyedService<HybridCache>(OrganizationCacheRegistration.CacheServiceKey).ShouldNotBeNull();

        using ServiceProvider generated = Build(new Dictionary<string, string?> { ["Cache:Mode"] = "Distributed" }, generation: true);
        generated.GetRequiredKeyedService<HybridCache>(OrganizationCacheRegistration.CacheServiceKey).ShouldNotBeNull();
        generated.GetRequiredKeyedService<IDistributedCache>(OrganizationCacheRegistration.DistributedServiceKey)
            .ShouldBeOfType<MemoryDistributedCache>();
    }

    [TestMethod]
    public void RuntimeDistributedConfigurationRequiresAuthenticatedEndpointAndSanitizesErrors()
    {
        const string privateValue = "private-configuration-material";
        foreach (string? connection in new[]
                 {
                     null, "", "cache:6379", "password=" + privateValue,
                     "cache:6379,password=" + privateValue + ",unknown=" + privateValue
                 })
        {
            InvalidOperationException failure = Should.Throw<InvalidOperationException>(() => Build(new Dictionary<string, string?>
            {
                ["Cache:Mode"] = "Distributed", ["ConnectionStrings:cache"] = connection
            }));
            failure.Message.ShouldNotContain(privateValue);
            failure.InnerException.ShouldBeNull();
        }
        // Resolving the lazy adapter cannot contact this deliberately closed endpoint.
        using ServiceProvider configured = Build(new Dictionary<string, string?>
        {
            ["Cache:Mode"] = "Distributed", ["ConnectionStrings:cache"] = "127.0.0.1:1,password=test-only-password"
        });
        configured.GetRequiredKeyedService<HybridCache>(OrganizationCacheRegistration.CacheServiceKey).ShouldNotBeNull();
    }

    [TestMethod]
    public void ProductNamespaceIsStableAcrossApiAndWorkerExecutableNames()
    {
        using ServiceProvider api = Build(new Dictionary<string, string?>(), applicationName: "Example.Api");
        using ServiceProvider worker = Build(new Dictionary<string, string?>(), applicationName: "Example.Worker");
        var apiIdentity = api.GetRequiredService<CacheHostIdentity>();
        var workerIdentity = worker.GetRequiredService<CacheHostIdentity>();
        apiIdentity.ApplicationNamespace.ShouldBe(workerIdentity.ApplicationNamespace);
        apiIdentity.Environment.ShouldBe(workerIdentity.Environment);
        using ServiceProvider explicitProduct = Build(new Dictionary<string, string?> { ["Cache:ApplicationNamespace"] = "another-product" });
        explicitProduct.GetRequiredService<CacheHostIdentity>().ApplicationNamespace.ShouldBe("another-product");
    }

    [TestMethod]
    public async Task LocalOnlyCompositionDoesNotUseAnotherFeaturesDefaultDistributedCache()
    {
        ObservedDefaultCache otherFeature = new();
        using ServiceProvider provider = Build(new Dictionary<string, string?> { ["Cache:Mode"] = "LocalOnly" }, otherFeature: otherFeature);
        HybridCache hybrid = provider.GetRequiredKeyedService<HybridCache>(OrganizationCacheRegistration.CacheServiceKey);

        (await hybrid.GetOrCreateAsync("composition-probe", _ => ValueTask.FromResult(42))).ShouldBe(42);
        await hybrid.RemoveAsync("composition-probe");

        otherFeature.Calls.ShouldBe(0);
    }

    private static ServiceProvider Build(
        IReadOnlyDictionary<string, string?> settings, bool generation = false, string applicationName = "Test.Api",
        IDistributedCache? otherFeature = null)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new CacheTestEnvironment { ApplicationName = applicationName });
        if (otherFeature is not null) services.AddSingleton(otherFeature);
        services.AddOrganizationCache(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), generation);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class CacheTestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Test.Api";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class ObservedDefaultCache : IDistributedCache
    {
        public int Calls { get; private set; }
        public byte[]? Get(string key) { Calls++; return null; }
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) { Calls++; return Task.FromResult<byte[]?>(null); }
        public void Refresh(string key) => Calls++;
        public Task RefreshAsync(string key, CancellationToken token = default) { Calls++; return Task.CompletedTask; }
        public void Remove(string key) => Calls++;
        public Task RemoveAsync(string key, CancellationToken token = default) { Calls++; return Task.CompletedTask; }
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => Calls++;
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        { Calls++; return Task.CompletedTask; }
    }
}
