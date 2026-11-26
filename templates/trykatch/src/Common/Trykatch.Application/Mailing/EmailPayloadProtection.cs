namespace Trykatch.Application.Mailing;

public enum EmailDeliveryBoundary
{
    Organization,
    Identity
}

/// <summary>Trusted ownership and immutable template identity bound to protected delivery content.</summary>
public sealed record EmailDeliveryProtectionContext(
    EmailDeliveryBoundary Boundary,
    Guid BoundaryOwnerId,
    Guid DeliveryId,
    string TemplateKey,
    int TemplateVersion);

/// <summary>Protects private delivery intent without exposing the host's key-ring implementation.</summary>
public interface IEmailPayloadProtector
{
    string Protect(EmailDeliveryProtectionContext context, string intent);
    string Unprotect(EmailDeliveryProtectionContext context, string protectedIntent);
}
