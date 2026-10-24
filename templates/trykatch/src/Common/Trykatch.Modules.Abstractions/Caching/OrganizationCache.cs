namespace Trykatch.Modules;

public enum ModuleCacheAudience
{
    Organization,
    CurrentActor
}

public sealed record ModuleCacheKey(
    string ModuleId,
    string ReadModelKey,
    int SchemaVersion,
    int PolicyVersion,
    string ParametersHash,
    ModuleCacheAudience Audience,
    string TtlPolicyKey);

public interface IOrganizationCache
{
    Task<T> GetOrCreateAsync<T>(
        ModuleCacheKey key,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken = default);

    Task InvalidateAsync(ModuleCacheKey key, CancellationToken cancellationToken = default);
}
