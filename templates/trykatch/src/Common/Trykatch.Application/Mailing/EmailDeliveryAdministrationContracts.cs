using Trykatch.Application.Common;

namespace Trykatch.Application.Mailing;

public sealed record EmailDeliveryView(Guid DeliveryId, string Boundary, Guid? OrganizationId,
    string TemplateKey, int? TemplateVersion, string State, int Attempts, DateTimeOffset CreatedAtUtc,
    DateTimeOffset? NextAttemptAtUtc, DateTimeOffset? CompletedAtUtc, string? FailureCode, int? SmtpStatusCode);

public sealed record EmailTemplateView(string TemplateKey, int TemplateVersion);
public sealed record EmailTemplatePreview(EmailTemplateView Template, string Subject, string TextBody, string? HtmlBody);

/// <summary>Only a validated bounded query can reach an owned metadata reader.</summary>
public sealed class EmailDeliveryQuery
{
    private EmailDeliveryQuery(EmailDeliveryBoundary boundary, int page, int pageSize, string? state, Guid? organizationId, Guid? deliveryId)
    { Boundary = boundary; Page = page; PageSize = pageSize; State = state; OrganizationId = organizationId; DeliveryId = deliveryId; }
    public EmailDeliveryBoundary Boundary { get; }
    public int Page { get; }
    public int PageSize { get; }
    public string? State { get; }
    public Guid? OrganizationId { get; }
    public Guid? DeliveryId { get; }

    public static EmailDeliveryQuery? Create(string boundary, int page, int pageSize, string? state, Guid? organizationId = null, Guid? deliveryId = null)
    {
        EmailDeliveryBoundary? selected = boundary switch { "organization" => EmailDeliveryBoundary.Organization, "identity" => EmailDeliveryBoundary.Identity, _ => null };
        if (selected is null || state is not (null or "queued" or "leased" or "delivered" or "suppressed" or "terminal")
            || organizationId == Guid.Empty || deliveryId == Guid.Empty || selected == EmailDeliveryBoundary.Identity && organizationId is not null)
            return null;
        return new(selected.Value, Math.Clamp(page, 1, 10_000), Math.Clamp(pageSize, 1, 100), state, organizationId, deliveryId);
    }
}

public interface IEmailDeliveryMetadataReader
{
    EmailDeliveryBoundary Boundary { get; }
    Task<Result<PagedResult<EmailDeliveryView>>> ListAsync(Guid actorId, EmailDeliveryQuery query, CancellationToken cancellationToken);
}

public interface IEmailTemplateCatalogue
{
    IReadOnlyList<EmailTemplateView> List();
    EmailTemplatePreview? Preview(string templateKey, int templateVersion);
}

public static class EmailDeliveryMetadataRules
{
    public static EmailDeliveryView View(Guid id, EmailDeliveryBoundary boundary, Guid? organizationId,
        string templateKey, int templateVersion, bool templateAvailable, string state, int attempts,
        DateTimeOffset created, DateTimeOffset? next, DateTimeOffset? completed, string? failure, int? smtpStatus) =>
        new(id, boundary == EmailDeliveryBoundary.Organization ? "organization" : "identity", organizationId,
            templateAvailable ? templateKey : "unavailable", templateAvailable ? templateVersion : null,
            state is "queued" or "leased" or "delivered" or "suppressed" or "terminal" ? state : "unknown",
            Math.Clamp(attempts, 0, EmailDeliveryPolicy.MaximumAttempts), created, next, completed,
            SafeFailure(failure), smtpStatus is >= 200 and < 600 ? smtpStatus : null);

    public static string? SafeFailure(string? value) => value is null or nameof(EmailFailureCode.None) ? null
        : Enum.TryParse(value, out EmailFailureCode code) && Enum.IsDefined(code) && code.ToString() == value ? value : "unclassified";
}
