using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Trykatch.Application.Mailing;

namespace Trykatch.Infrastructure.Modules.Email;

internal sealed class DataProtectionEmailPayloadProtector(IDataProtectionProvider protection) : IEmailPayloadProtector
{
    public string Protect(EmailDeliveryProtectionContext context, string intent) =>
        Protector(context).Protect(intent);

    public string Unprotect(EmailDeliveryProtectionContext context, string protectedIntent) =>
        Protector(context).Unprotect(protectedIntent);

    private IDataProtector Protector(EmailDeliveryProtectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Enum.IsDefined(context.Boundary) || context.BoundaryOwnerId == Guid.Empty || context.DeliveryId == Guid.Empty
            || context.TemplateVersion < 1 || string.IsNullOrWhiteSpace(context.TemplateKey)
            || context.TemplateKey.Length > 160 || context.TemplateKey.Any(char.IsControl))
            throw new ArgumentException("Email delivery protection context is invalid.", nameof(context));

        return protection.CreateProtector("Trykatch.EmailDelivery", "v1", context.Boundary.ToString(),
            context.BoundaryOwnerId.ToString("N"), context.DeliveryId.ToString("N"), context.TemplateKey,
            context.TemplateVersion.ToString(CultureInfo.InvariantCulture));
    }
}
