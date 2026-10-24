using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Caching;

internal sealed record CacheHostIdentity(string ApplicationNamespace, string Environment);

internal static class OrganizationCacheRegistration
{
    internal const string CacheServiceKey = "organization-read-cache";
    internal const string DistributedServiceKey = "organization-read-cache-l2";
    internal const string ConnectionName = "cache";

    internal static void AddOrganizationCache(
        this IServiceCollection services, IConfiguration configuration, bool isOpenApiGeneration)
    {
        OrganizationCacheOptions settings;
        try
        {
            settings = configuration.GetSection(OrganizationCacheOptions.SectionName)
                .Get<OrganizationCacheOptions>(binding => binding.ErrorOnUnknownConfiguration = true) ?? new();
        }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException("Organization cache configuration is invalid.");
        }
        ValidateOptionsResult validation = new OrganizationCacheOptionsValidator().Validate(Options.DefaultName, settings);
        if (validation.Failed)
            throw new OptionsValidationException(Options.DefaultName, typeof(OrganizationCacheOptions), validation.Failures);
        services.AddSingleton<IOptions<OrganizationCacheOptions>>(Options.Create(settings));
        services.AddSingleton(provider => new CacheHostIdentity(
            settings.ApplicationNamespace ?? "app-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                typeof(CacheGenerationStore).Assembly.GetName().Name!))).ToLowerInvariant(),
            provider.GetService<IHostEnvironment>()?.EnvironmentName ?? Environments.Production));

        if (settings.Mode == OrganizationCacheMode.Distributed && !isOpenApiGeneration)
        {
            ConfigurationOptions connection = ParseConnection(configuration.GetConnectionString(ConnectionName));
            services.AddKeyedSingleton<IDistributedCache>(DistributedServiceKey, (_, _) => new RedisCache(
                Options.Create(new RedisCacheOptions { ConfigurationOptions = connection, InstanceName = "organization-cache:" })));
        }
        else
        {
            // Give this cache its own backend identity, even if another feature registers an unkeyed
            // IDistributedCache. HybridCache recognizes the framework memory adapter as local-only.
            services.AddKeyedSingleton<IDistributedCache>(DistributedServiceKey, (_, _) =>
                new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
        }
        services.AddKeyedHybridCache(CacheServiceKey, options =>
        {
            options.DistributedCacheServiceKey = DistributedServiceKey;
            options.MaximumPayloadBytes = OrganizationCacheOptions.MaximumPayloadBytes;
            options.MaximumKeyLength = 1_024;
            options.ReportTagMetrics = false;
            options.DisableCompression = true;
        });
        services.AddScoped<IOrganizationCache, HybridOrganizationCache>();
    }

    private static ConfigurationOptions ParseConnection(string? value)
    {
        const string invalid = "Distributed organization caching requires a valid authenticated cache connection.";
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException(invalid);
        ConfigurationOptions connection;
        try { connection = ConfigurationOptions.Parse(value); }
        catch (ArgumentException) { throw new InvalidOperationException(invalid); }
        catch (FormatException) { throw new InvalidOperationException(invalid); }
        if (connection.EndPoints.Count != 1 || string.IsNullOrWhiteSpace(connection.Password))
            throw new InvalidOperationException(invalid);
        connection.AbortOnConnectFail = false;
        connection.AllowAdmin = false;
        connection.ConnectRetry = 1;
        connection.ConnectTimeout = 1_000;
        connection.AsyncTimeout = 1_000;
        connection.SyncTimeout = 1_000;
        connection.ClientName = "organization-cache";
        return connection;
    }
}
