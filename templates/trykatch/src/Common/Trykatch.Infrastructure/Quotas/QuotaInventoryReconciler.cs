using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Trykatch.Application.Quotas;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Quotas;

internal enum QuotaInventoryReconciliationState { InProgress, Complete, Paused, Unavailable }
internal sealed record QuotaInventoryReconciliationResult(QuotaInventoryReconciliationState State, string? FailureCode = null);

/// <summary>One bounded owner inventory page and its usage/checkpoint commit under the shared resource lock.</summary>
internal sealed class QuotaInventoryReconciler(QuotaMaintenanceScopeFactory scopeFactory)
{
    internal async Task<QuotaInventoryReconciliationResult> ReconcilePageAsync(Guid organizationId, string resourceKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            CancellationToken token = timeout.Token;
            await using QuotaMaintenanceScope? scope = await scopeFactory.TryCreateAsync(organizationId, token);
            if (scope is null) return new(QuotaInventoryReconciliationState.Paused, "quota_target_ineligible");
            IServiceProvider services = scope.Services;
            QuotaWorkflowRegistry registry = services.GetRequiredService<QuotaWorkflowRegistry>();
            ApplicationDbContext application = services.GetRequiredService<ApplicationDbContext>();
            OrganizationEntitlementStore entitlements = new(application, services.GetRequiredService<ModuleCatalog>());
            if (!registry.TryGetInventory(resourceKey, out ModuleQuotaInventorySourceBinding? binding)
                || !entitlements.TryGetResource(resourceKey, out string owner, out ModuleQuotaResourceDescriptor? resource)
                || resource?.InventorySource != binding!.Source)
                return new(QuotaInventoryReconciliationState.Paused, "quota_inventory_source_unavailable");
            QuotaPublicationAuthority publication = services.GetRequiredService<QuotaPublicationAuthority>();
            QuotaPublicationReceipt receipt = await publication.EnsurePublishedAsync(
                services.GetRequiredService<IOrganizationEntitlementProjectionPublisher>(), token);
            if (!await QuotaResourceLock.TryAcquireAsync(application, organizationId, resourceKey, token))
                return Unavailable("quota_busy");
            publication.ResourceLockAcquired();
            await scope.ValidateAsync(token);
            OrganizationEntitlementRecord? projection = await entitlements.ReadAsync(organizationId, resourceKey, token);
            if (projection is null || projection.OwnerModuleId != owner || projection.Unit != resource.Unit
                || !receipt.IsSatisfiedBy(projection)) return Unavailable("quota_projection_invalid");
            OrganizationQuotaInventoryRecord? checkpoint = await application.Set<OrganizationQuotaInventoryRecord>().AsNoTracking()
                .SingleOrDefaultAsync(row => row.OrganizationId == organizationId && row.ResourceKey == resourceKey, token);
            ModuleQuotaInventorySourceRef source = binding.Source;
            bool current = checkpoint?.SourceKey == source.SourceKey && checkpoint.SourceVersion == source.SourceVersion;
            if (current && checkpoint!.State == QuotaInventoryState.Complete)
                return new(QuotaInventoryReconciliationState.Complete);
            string? cursor = current ? checkpoint!.Cursor : null;
            ModuleQuotaInventoryPage page = await binding.Adapter.ReadPageAsync(cursor, 500, token);
            QuotaInventoryPageValidator.Validate(page, cursor);
            DateTimeOffset now = services.GetRequiredService<TimeProvider>().GetUtcNow();
            OrganizationUsageLedger ledger = new(application);
            await ledger.AppendInventoryAsync(organizationId, resourceKey, page.Entries, QuotaMaintenanceAuthority.ActorId, now, token);
            (long committed, long held) = await ledger.TotalsAsync(organizationId, resourceKey, token);
            if (committed < 0 || held < 0) return Unavailable("quota_usage_invalid");
            _ = checked(committed + held);
            int state = page.IsComplete ? (int)QuotaInventoryState.Complete : (int)QuotaInventoryState.Initializing;
            await application.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO app.organization_quota_inventory
                    ("OrganizationId", "ResourceKey", "SourceKey", "SourceVersion", "Cursor", "State", "UpdatedAtUtc")
                VALUES ({organizationId}, {resourceKey}, {source.SourceKey}, {source.SourceVersion}, {page.NextCursor}, {state}, {now})
                ON CONFLICT ("OrganizationId", "ResourceKey") DO UPDATE
                SET "SourceKey" = EXCLUDED."SourceKey", "SourceVersion" = EXCLUDED."SourceVersion", "Cursor" = EXCLUDED."Cursor",
                    "State" = EXCLUDED."State", "UpdatedAtUtc" = EXCLUDED."UpdatedAtUtc"
                """, token);
            await scope.CommitAsync(token);
            return new(page.IsComplete ? QuotaInventoryReconciliationState.Complete : QuotaInventoryReconciliationState.InProgress);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable("quota_inventory_unavailable");
        }
        catch (Exception failure) when (failure is OrganizationQuotaUnavailableException or DbException or TimeoutException
            or OverflowException or ArgumentException or InvalidOperationException { InnerException: DbException })
        {
            return Unavailable("quota_inventory_unavailable");
        }
    }

    private static QuotaInventoryReconciliationResult Unavailable(string code) => new(QuotaInventoryReconciliationState.Unavailable, code);
}
