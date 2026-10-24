using Microsoft.Extensions.Caching.Hybrid;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Trykatch.Application.Organizations;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Caching;

internal sealed class HybridOrganizationCache(
    [FromKeyedServices(OrganizationCacheRegistration.CacheServiceKey)] HybridCache cache,
    ModuleCatalog modules,
    IOrganizationContext organization,
    CacheGenerationStore generations,
    CacheInvalidationCommitState commitState,
    IOptions<OrganizationCacheOptions> options,
    CacheHostIdentity identity) : IOrganizationCache
{
    private const string InvalidScope = "Organization cache operations require a valid installed-module key and resolved actor-scoped organization.";
    private JsonSerializerOptions? payloadSerializerOptions;

    public Task<T> GetOrCreateAsync<T>(ModuleCacheKey key, Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(factory);
        string logicalHash = ValidateAndHash(key);

        return GetValidatedAsync(key, logicalHash, factory, cancellationToken);
    }

    public async Task InvalidateAsync(ModuleCacheKey key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string logicalHash = ValidateAndHash(key);
        Guid oldGeneration = await generations.InvalidateAsync(logicalHash, cancellationToken).ConfigureAwait(false);

        if (options.Value.Mode == OrganizationCacheMode.Disabled) return;

        string physicalKey = CacheKeyPolicy.PhysicalKey(logicalHash, oldGeneration);
        HybridCache evictionCache = cache;
        string evictionKey = physicalKey;
        commitState.Register(generations.Transaction, evictionKey,
            cancellation => evictionCache.RemoveAsync(evictionKey, cancellation));
    }

    private async Task<T> GetValidatedAsync<T>(ModuleCacheKey key, string logicalHash,
        Func<CancellationToken, Task<T>> factory, CancellationToken cancellationToken)
    {
        OrganizationCacheOptions configuration = options.Value;
        if (configuration.Mode == OrganizationCacheMode.Disabled)
            return await factory(cancellationToken).ConfigureAwait(false);

        CacheReadGeneration read = await generations.ReadAsync(logicalHash, cancellationToken).ConfigureAwait(false);
        if (!read.CanCache)
            return await factory(cancellationToken).ConfigureAwait(false);

        string physicalKey = CacheKeyPolicy.PhysicalKey(logicalHash, read.Generation);
        JsonSerializerOptions serializerOptions = payloadSerializerOptions ??= generations.CreatePayloadSerializerOptions();
        int ttlSeconds = configuration.TtlSeconds[key.TtlPolicyKey];
        TimeSpan ttl = TimeSpan.FromSeconds(ttlSeconds);
        HybridCacheEntryOptions entryOptions = new()
        {
            Expiration = ttl,
            LocalCacheExpiration = ttl,
            Flags = configuration.Mode == OrganizationCacheMode.LocalOnly
                ? HybridCacheEntryFlags.DisableDistributedCache
                : HybridCacheEntryFlags.None
        };

        byte[] payload;
        try
        {
            payload = await cache.GetOrCreateAsync<FactoryState<T>, byte[]>(
                physicalKey,
                new(factory, configuration.FactoryTimeout, generations.CanPublishSourceResult, serializerOptions),
                static async (state, _) => await CreatePayloadAsync(state).ConfigureAwait(false),
                entryOptions,
                tags: null,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch (UncachedSourcePayloadException uncached)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return DecodeSourceResult<T>(uncached.Payload, serializerOptions);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (payload.Length <= OrganizationCacheOptions.MaximumPayloadBytes
            && CachePayloadCodec.TryDecode(payload, out T? cachedValue, serializerOptions))
        {
            return cachedValue!;
        }

        byte[] detachedPayload = await CreateDetachedPayloadAsync(factory, configuration.FactoryTimeout, serializerOptions).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return DecodeSourceResult<T>(detachedPayload, serializerOptions);
    }

    private string ValidateAndHash(ModuleCacheKey key)
    {
        if (!organization.IsResolved || organization.OrganizationId == Guid.Empty || organization.ActorId == Guid.Empty
            || key is null || !modules.Contains(key.ModuleId)
            || !options.Value.TtlSeconds.ContainsKey(key.TtlPolicyKey))
        {
            throw new InvalidOperationException(InvalidScope);
        }

        try
        {
            return CacheKeyPolicy.LogicalHash(key, identity.ApplicationNamespace, identity.Environment,
                organization.OrganizationId, organization.ActorId);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException(InvalidScope);
        }
    }

    private static async Task<byte[]> CreatePayloadAsync<T>(FactoryState<T> state)
    {
        using CancellationTokenSource timeout = new(state.Timeout);
        T value = await state.Factory(timeout.Token).ConfigureAwait(false);
        timeout.Token.ThrowIfCancellationRequested();
        bool canPublish = state.CanPublish();
        byte[] payload = CachePayloadCodec.Encode(value, state.SerializerOptions);
        if (!canPublish || payload.Length > OrganizationCacheOptions.MaximumPayloadBytes)
            throw new UncachedSourcePayloadException(payload);
        return payload;
    }

    private static async Task<byte[]> CreateDetachedPayloadAsync<T>(
        Func<CancellationToken, Task<T>> factory, TimeSpan timeoutDuration, JsonSerializerOptions serializerOptions)
    {
        using CancellationTokenSource timeout = new(timeoutDuration);
        T value = await factory(timeout.Token).ConfigureAwait(false);
        timeout.Token.ThrowIfCancellationRequested();
        return CachePayloadCodec.Encode(value, serializerOptions);
    }

    private static T DecodeSourceResult<T>(byte[] payload, JsonSerializerOptions serializerOptions)
    {
        if (!CachePayloadCodec.TryDecode(payload, out T? value, serializerOptions))
            throw new InvalidOperationException("Organization cache source value could not be detached.");
        return value!;
    }

    private sealed record FactoryState<T>(
        Func<CancellationToken, Task<T>> Factory, TimeSpan Timeout, Func<bool> CanPublish, JsonSerializerOptions SerializerOptions);

    private sealed class UncachedSourcePayloadException(byte[] payload) : Exception("Organization cache source value is not eligible for storage.")
    {
        public byte[] Payload { get; } = payload;
    }
}
