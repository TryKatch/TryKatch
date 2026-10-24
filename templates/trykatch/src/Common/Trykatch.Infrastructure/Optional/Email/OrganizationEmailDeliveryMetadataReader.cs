using Microsoft.EntityFrameworkCore;
using Trykatch.Application.Common;
using Trykatch.Application.Identity;
using Trykatch.Application.Mailing;
using Trykatch.Infrastructure.Mailing;

namespace Trykatch.Infrastructure.Modules.Email;

internal sealed class OrganizationEmailDeliveryMetadataReader(
    DbContextOptions<OrganizationEmailDeliveryDbContext> options, IPlatformAccessDirectory access,
    EmailTemplateRegistry templates) : IEmailDeliveryMetadataReader
{
    public EmailDeliveryBoundary Boundary => EmailDeliveryBoundary.Organization;

    public async Task<Result<PagedResult<EmailDeliveryView>>> ListAsync(Guid actorId, EmailDeliveryQuery query, CancellationToken cancellationToken)
    {
        if (!(await access.ResolveEffectiveAccessAsync(actorId, cancellationToken)).HasPermission(PlatformPermissions.EmailRead))
            return Result.Failure<PagedResult<EmailDeliveryView>>("forbidden", "Email inspection requires explicit permission.");
        if (query.Boundary != Boundary)
            return Result.Failure<PagedResult<EmailDeliveryView>>("invalid_query", "Invalid email boundary.");
        await using OrganizationEmailDeliveryDbContext database = new(options);
        IQueryable<OrganizationEmailDeliveryRecord> records = database.EmailDeliveries.AsNoTracking();
        if (query.State is not null) records = records.Where(value => value.State == query.State);
        if (query.OrganizationId is not null) records = records.Where(value => value.OrganizationId == query.OrganizationId);
        if (query.DeliveryId is not null) records = records.Where(value => value.Id == query.DeliveryId);
        long count = await records.LongCountAsync(cancellationToken);
        // Projection intentionally excludes the protected packet and all execution/lease provenance.
        var rows = await records.OrderByDescending(value => value.CreatedAtUtc).ThenByDescending(value => value.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .Select(value => new { value.Id, value.OrganizationId, value.TemplateKey, value.TemplateVersion,
                value.State, value.Attempts, value.CreatedAtUtc, value.NextAttemptAtUtc, value.CompletedAtUtc,
                value.LastFailureCode, value.LastSmtpStatusCode }).ToArrayAsync(cancellationToken);
        HashSet<EmailTemplateView> knownTemplates = templates.Templates
            .Select(value => new EmailTemplateView($"{value.OwnerModuleId}/{value.TemplateKey}", value.TemplateVersion)).ToHashSet();
        knownTemplates.Add(new("core.organization-invitation", 1));
        EmailDeliveryView[] items = rows.Select(value => EmailDeliveryMetadataRules.View(value.Id, Boundary, value.OrganizationId,
            value.TemplateKey, value.TemplateVersion, knownTemplates.Contains(new(value.TemplateKey, value.TemplateVersion)),
            value.State, value.Attempts, value.CreatedAtUtc, value.NextAttemptAtUtc, value.CompletedAtUtc,
            value.LastFailureCode, value.LastSmtpStatusCode)).ToArray();
        return Result.Success(new PagedResult<EmailDeliveryView>(items, query.Page, query.PageSize, count));
    }
}
