namespace Trykatch.Application.Quotas;

public enum OrganizationRequestAdmissionDisposition { Allowed, Exhausted, Unavailable }

public sealed record OrganizationRequestAdmissionResult(
    OrganizationRequestAdmissionDisposition Disposition, int RetryAfterSeconds = 0);

/// <summary>Host request admission for the trusted current organization, independent of business rollback.</summary>
public interface IOrganizationRequestAdmission
{
    bool IsEnabled { get; }
    Task<OrganizationRequestAdmissionResult> TryAdmitAsync(CancellationToken cancellationToken = default);
}
