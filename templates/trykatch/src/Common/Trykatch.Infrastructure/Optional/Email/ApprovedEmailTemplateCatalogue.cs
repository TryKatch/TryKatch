using Microsoft.Extensions.DependencyInjection;
using Trykatch.Application.Mailing;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Modules.Email;

/// <summary>Compiled templates render only their declared synthetic fixture data.</summary>
internal sealed class ApprovedEmailTemplateCatalogue(IServiceProvider services,
    PasswordResetEmailRendering passwordReset, InvitationEmailRendering invitation) : IEmailTemplateCatalogue
{
    public IReadOnlyList<EmailTemplateView> List() => new[]
        { new EmailTemplateView("core.organization-invitation", 1), new("core.password-reset", 1) }
        .Concat(services.GetService<EmailTemplateRegistry>()?.Templates.Select(value =>
            new EmailTemplateView($"{value.OwnerModuleId}/{value.TemplateKey}", value.TemplateVersion)) ?? [])
        .OrderBy(value => value.TemplateKey, StringComparer.Ordinal).ThenBy(value => value.TemplateVersion).ToArray();

    public EmailTemplatePreview? Preview(string templateKey, int templateVersion)
    {
        if (templateVersion == 1 && templateKey == "core.password-reset")
            return FromPrepared(templateKey, passwordReset.Render("preview@example.invalid", "Example member",
                "https://example.invalid/reset-password?email=preview%40example.invalid&token=synthetic-preview"));
        if (templateVersion == 1 && templateKey == "core.organization-invitation")
            return FromPrepared(templateKey, invitation.Render("preview@example.invalid", "Example organization", "Example role",
                "https://example.invalid/invitation?token=synthetic-preview"));
        EmailTemplateRegistry? registry = services.GetService<EmailTemplateRegistry>();
        ModuleEmailTemplateDescriptor? descriptor = registry?.Templates.SingleOrDefault(value =>
            $"{value.OwnerModuleId}/{value.TemplateKey}" == templateKey && value.TemplateVersion == templateVersion);
        if (descriptor is null || !registry!.TryPreview(new ModuleEmailTemplateRef(descriptor.OwnerModuleId,
            descriptor.TemplateKey, descriptor.TemplateVersion), out ModuleEmailContent? content)) return null;
        return new(new(templateKey, templateVersion), content!.Subject, content.TextBody, content.HtmlBody);
    }

    private static EmailTemplatePreview FromPrepared(string key, EmailPreparedMessage message) =>
        new(new(key, 1), message.Subject, message.TextBody, message.HtmlBody);
}
