using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Trykatch.Api.Security;

/// <summary>Protects organization connection capacity before scope/transaction middleware runs.</summary>
public sealed class AssistantRequestLimits : IDisposable
{
    public const int ConcurrentRequests = 8;
    private readonly PartitionedRateLimiter<HttpContext> _concurrency = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        context.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName == "assistant"
            ? RateLimitPartition.GetConcurrencyLimiter("assistant", _ => new ConcurrencyLimiterOptions { PermitLimit = ConcurrentRequests, QueueLimit = 0 })
            : RateLimitPartition.GetNoLimiter("other"));
    private readonly PartitionedRateLimiter<HttpContext> _global = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    public AssistantRequestLimits() => Limiter = PartitionedRateLimiter.CreateChained(_concurrency, _global);
    public PartitionedRateLimiter<HttpContext> Limiter { get; }
    public void Dispose()
    {
        Limiter.Dispose();
        // CreateChained deliberately does not own/dispose its inner limiters.
        _concurrency.Dispose();
        _global.Dispose();
    }
}
