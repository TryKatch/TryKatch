using Trykatch.Application.Common;
using Trykatch.Application.Identity;

namespace Trykatch.Application.Mailing;

/// <summary>Read-only operator access. Authorize before inspecting any delivery store or rendering fixtures.</summary>
public sealed class EmailDeliveryAdministration(IPlatformAccessDirectory access,
    IEnumerable<IEmailDeliveryMetadataReader> readers, IEnumerable<IEmailTemplateCatalogue> catalogues)
{
    public async Task<Result<PagedResult<EmailDeliveryView>>> ListAsync(Guid actorId, string boundary,
        int page, int pageSize, string? state, Guid? organizationId, Guid? deliveryId, CancellationToken cancellationToken)
    {
        if (!await MayReadAsync(actorId, cancellationToken)) return Denied<PagedResult<EmailDeliveryView>>();
        EmailDeliveryQuery? query = EmailDeliveryQuery.Create(boundary, page, pageSize, state, organizationId, deliveryId);
        if (query is null) return Result.Failure<PagedResult<EmailDeliveryView>>("invalid_query", "Invalid email delivery filter.");
        IEmailDeliveryMetadataReader? reader = readers.SingleOrDefault(value => value.Boundary == query.Boundary);
        return reader is null ? Unavailable<PagedResult<EmailDeliveryView>>() : await reader.ListAsync(actorId, query, cancellationToken);
    }

    public async Task<Result<PagedResult<EmailTemplateView>>> TemplatesAsync(Guid actorId, int page, int pageSize, CancellationToken cancellationToken)
    {
        if (!await MayReadAsync(actorId, cancellationToken)) return Denied<PagedResult<EmailTemplateView>>();
        IEmailTemplateCatalogue? catalogue = catalogues.SingleOrDefault();
        if (catalogue is null) return Unavailable<PagedResult<EmailTemplateView>>();
        page = Math.Clamp(page, 1, 10_000);
        pageSize = Math.Clamp(pageSize, 1, 100);
        IReadOnlyList<EmailTemplateView> templates = catalogue.List();
        return Result.Success(new PagedResult<EmailTemplateView>(templates.Skip((page - 1) * pageSize).Take(pageSize).ToArray(), page, pageSize, templates.Count));
    }

    public async Task<Result<EmailTemplatePreview>> PreviewAsync(Guid actorId, string templateKey, int templateVersion, CancellationToken cancellationToken)
    {
        if (!await MayReadAsync(actorId, cancellationToken)) return Denied<EmailTemplatePreview>();
        if (string.IsNullOrWhiteSpace(templateKey) || templateKey.Length > 160 || templateKey.Any(char.IsControl) || templateVersion < 1)
            return Result.Failure<EmailTemplatePreview>("invalid_query", "Invalid email template reference.");
        IEmailTemplateCatalogue? catalogue = catalogues.SingleOrDefault();
        if (catalogue is null) return Unavailable<EmailTemplatePreview>();
        EmailTemplatePreview? preview = catalogue.Preview(templateKey, templateVersion);
        return preview is null ? Result.Failure<EmailTemplatePreview>("not_found", "Email template is unavailable.") : Result.Success(preview);
    }

    private async Task<bool> MayReadAsync(Guid actorId, CancellationToken cancellationToken) =>
        (await access.ResolveEffectiveAccessAsync(actorId, cancellationToken)).HasPermission(PlatformPermissions.EmailRead);
    private static Result<T> Denied<T>() => Result.Failure<T>("forbidden", "Email inspection requires explicit permission.");
    private static Result<T> Unavailable<T>() => Result.Failure<T>("unavailable", "Email inspection is unavailable.");
}
