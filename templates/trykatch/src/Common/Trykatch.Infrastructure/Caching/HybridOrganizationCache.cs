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
        OrganizationCacheTelemetry.Invalidation(options.Value.Mode);

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
        {
            OrganizationCacheTelemetry.Read(configuration.Mode, eligible: false);
            OrganizationCacheTelemetry.Source(configuration.Mode);
            return await factory(cancellationToken).ConfigureAwait(false);
        }

        CacheReadGeneration read = await generations.ReadAsync(logicalHash, cancellationToken).ConfigureAwait(false);
        if (!read.CanCache)
        {
            OrganizationCacheTelemetry.Read(configuration.Mode, eligible: false);
            OrganizationCacheTelemetry.Source(configuration.Mode);
            return await factory(cancellationToken).ConfigureAwait(false);
        }

        OrganizationCacheTelemetry.Read(configuration.Mode, eligible: true);

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

        FactoryState<T> invocation = new(factory, configuration.FactoryTimeout, generations.CanPublishSourceResult,
            serializerOptions, configuration.Mode);
        byte[] payload;
        try
        {
            payload = await cache.GetOrCreateAsync<FactoryState<T>, byte[]>(
                physicalKey,
                invocation,
                static async (state, _) => await CreatePayloadAsync(state).ConfigureAwait(false),
                entryOptions,
                tags: null,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch (UncachedSourcePayloadException uncached)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReferenceEquals(uncached.Owner, invocation))
                return DecodeSourceResult<T>(uncached.Payload, serializerOptions);

            // A refused owner value may contain private tracked changes. Coalesced waiters
            // must use their own still-live scope; their source has not executed yet.
            OrganizationCacheTelemetry.Source(configuration.Mode);
            byte[] ownPayload = await CreateDetachedPayloadAsync(factory, configuration.FactoryTimeout, serializerOptions).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return DecodeSourceResult<T>(ownPayload, serializerOptions);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (payload.Length <= OrganizationCacheOptions.MaximumPayloadBytes
            && CachePayloadCodec.TryDecode(payload, out T? cachedValue, serializerOptions))
        {
            return cachedValue!;
        }

        OrganizationCacheTelemetry.Corrupt(configuration.Mode);
        OrganizationCacheTelemetry.Source(configuration.Mode);
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
        OrganizationCacheTelemetry.Source(state.Mode);
        T value = await state.Factory(timeout.Token).ConfigureAwait(false);
        timeout.Token.ThrowIfCancellationRequested();
        byte[] payload = CachePayloadCodec.Encode(value, state.SerializerOptions);
        timeout.Token.ThrowIfCancellationRequested();
        // Serialization can evaluate deferred DTO sequences and property getters. Inspect
        // tracking only after that source work has finished, before either tier can publish.
        bool canPublish = state.CanPublish();
        if (!canPublish || payload.Length > OrganizationCacheOptions.MaximumPayloadBytes)
        {
            if (!canPublish) OrganizationCacheTelemetry.Dirty(state.Mode);
            else OrganizationCacheTelemetry.Oversized(state.Mode);
            throw new UncachedSourcePayloadException(payload, state);
        }
        return payload;
    }

    private static async Task<byte[]> CreateDetachedPayloadAsync<T>(
        Func<CancellationToken, Task<T>> factory, TimeSpan timeoutDuration, JsonSerializerOptions serializerOptions)
    {
        using CancellationTokenSource timeout = new(timeoutDuration);
        T value = await factory(timeout.Token).ConfigureAwait(false);
        timeout.Token.ThrowIfCancellationRequested();
        byte[] payload = CachePayloadCodec.Encode(value, serializerOptions);
        timeout.Token.ThrowIfCancellationRequested();
        return payload;
    }

    private static T DecodeSourceResult<T>(byte[] payload, JsonSerializerOptions serializerOptions)
    {
        if (!CachePayloadCodec.TryDecode(payload, out T? value, serializerOptions))
            throw new InvalidOperationException("Organization cache source value could not be detached.");
        return value!;
    }

    private sealed record FactoryState<T>(
        Func<CancellationToken, Task<T>> Factory, TimeSpan Timeout, Func<bool> CanPublish, JsonSerializerOptions SerializerOptions, OrganizationCacheMode Mode);

    private sealed class UncachedSourcePayloadException(byte[] payload, object owner) : Exception("Organization cache source value is not eligible for storage.")
    {
        public byte[] Payload { get; } = payload;
        public object Owner { get; } = owner;
    }
}
