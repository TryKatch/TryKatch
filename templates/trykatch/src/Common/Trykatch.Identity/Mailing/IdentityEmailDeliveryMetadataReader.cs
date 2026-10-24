using Microsoft.EntityFrameworkCore;
using Trykatch.Application.Common;
using Trykatch.Application.Identity;
using Trykatch.Application.Mailing;

namespace Trykatch.Identity;

internal sealed class IdentityEmailDeliveryMetadataReader(
    IPlatformAccessDirectory accessDirectory,
    DbContextOptions<IdentityEmailDeliveryDbContext> options) : IEmailDeliveryMetadataReader
{
    public EmailDeliveryBoundary Boundary => EmailDeliveryBoundary.Identity;

    public async Task<Result<PagedResult<EmailDeliveryView>>> ListAsync(
        Guid actorId,
        EmailDeliveryQuery query,
        CancellationToken cancellationToken)
    {
        EffectivePlatformAccess access = await accessDirectory.ResolveEffectiveAccessAsync(actorId, cancellationToken);
        if (!access.HasPermission(PlatformPermissions.EmailRead))
            return Result.Failure<PagedResult<EmailDeliveryView>>(
                "forbidden", "Current platform access cannot read email delivery metadata.");

        if (query is null || query.Boundary != EmailDeliveryBoundary.Identity || query.OrganizationId is not null)
            return Result.Failure<PagedResult<EmailDeliveryView>>(
                "invalid_request", "Identity delivery query is invalid.");

        await using IdentityEmailDeliveryDbContext context = new(options);
        IQueryable<IdentityEmailDeliveryRecord> deliveries = context.EmailDeliveries.AsNoTracking();
        if (query.State is not null)
            deliveries = deliveries.Where(delivery => delivery.State == query.State);
        if (query.DeliveryId is Guid deliveryId)
            deliveries = deliveries.Where(delivery => delivery.Id == deliveryId);

        long totalCount = await deliveries.LongCountAsync(cancellationToken);
        var rows = await deliveries
            .OrderByDescending(delivery => delivery.CreatedAtUtc)
            .ThenByDescending(delivery => delivery.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(delivery => new
            {
                delivery.Id,
                delivery.TemplateKey,
                delivery.TemplateVersion,
                delivery.State,
                delivery.Attempts,
                delivery.CreatedAtUtc,
                delivery.NextAttemptAtUtc,
                delivery.CompletedAtUtc,
                delivery.LastFailureCode,
                delivery.LastSmtpStatusCode
            })
            .ToArrayAsync(cancellationToken);

        EmailDeliveryView[] items = rows.Select(delivery => EmailDeliveryMetadataRules.View(
            delivery.Id,
            EmailDeliveryBoundary.Identity,
            organizationId: null,
            delivery.TemplateKey,
            delivery.TemplateVersion,
            templateAvailable: delivery.TemplateKey == "core.password-reset" && delivery.TemplateVersion == 1,
            delivery.State,
            delivery.Attempts,
            delivery.CreatedAtUtc,
            delivery.NextAttemptAtUtc,
            delivery.CompletedAtUtc,
            delivery.LastFailureCode,
            delivery.LastSmtpStatusCode)).ToArray();

        return Result.Success(new PagedResult<EmailDeliveryView>(items, query.Page, query.PageSize, totalCount));
    }
}
