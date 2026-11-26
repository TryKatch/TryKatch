using Trykatch.Application.Common;

namespace Trykatch.Application.Eventing;

public sealed record ModuleEventDeliveryView(
    Guid DeliveryId,
    Guid MessageId,
    Guid OrganizationId,
    string ConsumerKey,
    string State,
    int Attempts,
    int ReplayGeneration,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? NextAttemptAtUtc,
    string FailureCode);

public interface IModuleEventDeliveryDirectory
{
    Task<Result<PagedResult<ModuleEventDeliveryView>>> ListAsync(
        Guid actorId,
        int page,
        int pageSize,
        string? state,
        CancellationToken cancellationToken = default);
}
