#if TRYKATCH_EMAIL
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Trykatch.Application.Identity;
using Trykatch.Infrastructure.Modules.Email;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class InvitationEmailTests
{
    [TestMethod]
    [DataRow("Viewer")]
    [DataRow("Review <team> & operations")]
    public async Task InvitationIncludesFriendlyRoleInPlainTextAndEscapedHtml(string roleName)
    {
        ServiceCollection services = new();
        services.AddEmailModule(new ConfigurationBuilder().Build(), isDevelopment: true);
        RecordingSender sender = new();
        services.AddSingleton<IEmailSender>(sender);
        using ServiceProvider provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IInvitationNotifier>().SendOrganizationInvitationAsync(
            "invitee@example.test", "Example workspace", roleName, "https://app.example.test/invite/fixture");
        EmailMessage message = sender.Message.ShouldNotBeNull();
        message.TextBody.ShouldContain($"Workspace role: {roleName}");
        message.HtmlBody.ShouldNotBeNull().ShouldContain(System.Net.WebUtility.HtmlEncode(roleName));
        message.HtmlBody.ShouldNotContain("<team>");
    }

    [TestMethod]
    public async Task InvitationAndRecoveryShareConfiguredApplicationBranding()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Email:Branding:ApplicationName"] = "Kamenta & partners",
            ["Email:Branding:AccentColor"] = "#245a48",
            ["Email:Branding:LogoUrl"] = "https://app.example.test/logo.png?size=32&format=png"
        }).Build();
        ServiceCollection services = new();
        services.AddEmailModule(configuration, isDevelopment: true);
        RecordingSender sender = new();
        services.AddSingleton<IEmailSender>(sender);
        using ServiceProvider provider = services.BuildServiceProvider();
        const string url = "https://app.example.test/invite/fixture?token=sample&next=welcome";
        await provider.GetRequiredService<IInvitationNotifier>().SendOrganizationInvitationAsync(
            "invitee@example.test", "Example <workspace>", "Review <team>", url);
        EmailMessage invitation = sender.Message.ShouldNotBeNull();
        invitation.Subject.ShouldBe("Join Example <workspace> on Kamenta & partners");
        string html = invitation.HtmlBody.ShouldNotBeNull();
        html.ShouldContain("Kamenta &amp; partners");
        html.ShouldContain("#245a48");
        html.ShouldContain("logo.png?size=32&amp;format=png");
        html.ShouldContain("Example &lt;workspace&gt;");
        html.ShouldContain("Review &lt;team&gt;");
        html.ShouldContain("token=sample&amp;next=welcome");
        html.ShouldContain("copy and paste this link");
        html.ShouldContain("role=\"presentation\"");
        html.ShouldNotContain("<workspace>");
        invitation.TextBody.ShouldContain(url);

        await provider.GetRequiredService<IAccountRecoveryNotifier>().SendPasswordResetAsync(
            "invitee@example.test", "Name <script>", "https://app.example.test/reset/fixture");
        EmailMessage recovery = sender.Message.ShouldNotBeNull();
        recovery.Subject.ShouldBe("Reset your Kamenta & partners password");
        string recoveryHtml = recovery.HtmlBody.ShouldNotBeNull();
        recoveryHtml.ShouldContain("Kamenta &amp; partners");
        recoveryHtml.ShouldContain("#245a48");
        recoveryHtml.ShouldContain("Name &lt;script&gt;");
        recoveryHtml.ShouldNotContain("<script>");
    }

    [TestMethod]
    [DataRow("ApplicationName", "")]
    [DataRow("ApplicationName", "Injected\r\nHeader")]
    [DataRow("AccentColor", "red; background:url(example)")]
    [DataRow("AccentColor", "#gggggg")]
    [DataRow("LogoUrl", "javascript:alert(1)")]
    [DataRow("LogoUrl", "http://example.test/logo.png")]
    [DataRow("LogoUrl", "https://user:password@example.test/logo.png")]
    public void InvalidBrandingIsRejectedWithoutDisclosingConfiguredValue(string property, string value)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [$"Email:Branding:{property}"] = value }).Build();
        ServiceCollection services = new();
        services.AddEmailModule(configuration, isDevelopment: true);
        using ServiceProvider provider = services.BuildServiceProvider();
        OptionsValidationException failure = Should.Throw<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<EmailBrandingOptions>>().Value);
        failure.Message.ShouldContain("Email branding is invalid");
        if (value.Length > 0) failure.Message.ShouldNotContain(value);
    }

    private sealed class RecordingSender : IEmailSender
    {
        public EmailMessage? Message { get; private set; }
        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Message = message;
            return Task.CompletedTask;
        }
    }
}
#endif
