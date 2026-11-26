using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Trykatch.Application.Common;
using Trykatch.Application.Eventing;
using Trykatch.Application.Identity;
using Trykatch.Infrastructure.Execution;
using Trykatch.Infrastructure.Persistence;

namespace Trykatch.Infrastructure.Eventing;

internal sealed class ModuleEventDeliveryDirectory(
    IConfiguration configuration,
    IPlatformAccessDirectory accessDirectory) : IModuleEventDeliveryDirectory
{
    private static readonly HashSet<string> AllowedStates = new(StringComparer.Ordinal)
    {
        "queued", "leased", "paused", "completed", "terminal"
    };

    private static readonly HashSet<string> KnownFailureCodes = new(StringComparer.Ordinal)
    {
        "acknowledgement_failed", "attempts_exhausted", "automatic_retry_not_supported", "completion_mismatch",
        "consumer_registration_invalid", "consumer_unavailable", "delivery_context_invalid", "delivery_state_invalid",
        "event_consumers_unavailable", "event_delivery_snapshot_mismatch", "event_execution_failed",
        "event_source_incompatible", "execution_kind_invalid", "execution_timeout", "inbox_mismatch",
        "invalid_request", "lease_lost", "module_unavailable", "operation_adapter_unavailable", "operation_unavailable",
        "organization_inactive", "organization_unavailable", "outbox_context_invalid", "outbox_source_missing",
        "outbox_source_mismatch", "payload_invalid", "permission_denied", "placement_not_supported", "resource_denied",
        "runtime_database_contract_invalid", "source_event_invalid", "work_context_invalid"
    };

    public async Task<Result<PagedResult<ModuleEventDeliveryView>>> ListAsync(
        Guid actorId,
        int page,
        int pageSize,
        string? state,
        CancellationToken cancellationToken = default)
    {
        if (state is not null && !AllowedStates.Contains(state))
            return Result.Failure<PagedResult<ModuleEventDeliveryView>>("invalid_request", "Delivery state is invalid.");

        EffectivePlatformAccess access = await accessDirectory.ResolveEffectiveAccessAsync(actorId, cancellationToken);
        if (!access.HasPermission(PlatformPermissions.OutboxRead))
            return Result.Failure<PagedResult<ModuleEventDeliveryView>>("forbidden", "Current platform access cannot read module event deliveries.");

        string connectionString = configuration.GetConnectionString(RuntimeDatabaseConnectionContract.WorkCoordinator)
            ?? throw new InvalidOperationException("Production connection string 'trykatch-work' is required.");
        await using ModuleEventDbContext dbContext = new(
            new DbContextOptionsBuilder<ModuleEventDbContext>().UseNpgsql(connectionString).Options);

        int boundedPage = Math.Clamp(page, 1, 10_000);
        int boundedSize = Math.Clamp(pageSize, 1, 100);
        IQueryable<ModuleEventDelivery> deliveries = dbContext.Deliveries.AsNoTracking();
        if (state is not null)
            deliveries = deliveries.Where(delivery => delivery.State == state);

        long totalCount = await deliveries.LongCountAsync(cancellationToken);
        var rows = await deliveries
            .OrderByDescending(delivery => delivery.CreatedAtUtc)
            .ThenByDescending(delivery => delivery.Id)
            .Skip((boundedPage - 1) * boundedSize)
            .Take(boundedSize)
            .Select(delivery => new
            {
                delivery.Id,
                delivery.MessageId,
                delivery.OrganizationId,
                delivery.ConsumerKey,
                delivery.State,
                delivery.Attempts,
                delivery.ReplayGeneration,
                delivery.CreatedAtUtc,
                delivery.NextAttemptAtUtc,
                delivery.LastFailureCode
            })
            .ToArrayAsync(cancellationToken);

        ModuleEventDeliveryView[] items = rows.Select(delivery => new ModuleEventDeliveryView(
            delivery.Id,
            delivery.MessageId,
            delivery.OrganizationId,
            delivery.ConsumerKey,
            delivery.State,
            delivery.Attempts,
            delivery.ReplayGeneration,
            delivery.CreatedAtUtc,
            delivery.NextAttemptAtUtc,
            SafeFailureCode(delivery.LastFailureCode))).ToArray();

        return Result.Success(new PagedResult<ModuleEventDeliveryView>(items, boundedPage, boundedSize, totalCount));
    }

    private static string SafeFailureCode(string? value) => value is not null && KnownFailureCodes.Contains(value)
        ? value
        : "unclassified";
}
