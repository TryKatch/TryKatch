#if TRYKATCH_EMAIL
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trykatch.Application.Mailing;
using Trykatch.Infrastructure.Modules.Email;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class InvitationEmailRenderingTests
{
    [TestMethod]
    public void RenderPreservesTheEstablishedInvitationSubjectPlainTextAndBrandedHtml()
    {
        const string recipient = "invitee@example.test";
        const string organizationName = "Example <workspace>";
        const string roleName = "Review <team> & operations";
        const string url = "https://app.example.test/invite/fixture?token=sample&next=welcome";
        IConfiguration configuration = Configuration();
        ServiceCollection services = new();
        services.AddEmailModule(configuration, isDevelopment: true);
        using ServiceProvider provider = services.BuildServiceProvider();

        EmailPreparedMessage message = new InvitationEmailRendering(provider.GetRequiredService<BrandedEmailTemplate>())
            .Render(recipient, organizationName, roleName, url);

        message.Recipient.ShouldBe(recipient);
        message.Subject.ShouldBe("Join Example <workspace> on Kamenta & partners");
        message.TextBody.ShouldBe("You have been invited to join Example <workspace>.\n\nWorkspace role: Review <team> & operations\n\nAccept the invitation and set up your account:\nhttps://app.example.test/invite/fixture?token=sample&next=welcome\n\nThis invitation can be accepted once. If you were not expecting it, you can safely ignore this message.");
        string safeOrganization = System.Net.WebUtility.HtmlEncode(organizationName);
        string safeRole = System.Net.WebUtility.HtmlEncode(roleName);
        string expectedHtml = provider.GetRequiredService<BrandedEmailTemplate>().Render(
            "You're invited", $"Join {organizationName} as {roleName}.",
            $"<p>You have been invited to join <strong>{safeOrganization}</strong>.</p><p style=\"padding:16px;background-color:#f8faf9;border:1px solid #dce3e1;border-radius:4px;\">Workspace role: <strong>{safeRole}</strong></p><p>Accept the invitation and set up your account to get started.</p>",
            "Accept invitation", url,
            "This invitation can be accepted once. If you were not expecting it, you can safely ignore this message.");
        message.HtmlBody.ShouldBe(expectedHtml);
    }

    [TestMethod]
    public void RenderEncodesDynamicHtmlKeepsTheRecipientAndTextValuesAndDoesNotSend()
    {
        const string recipient = "dynamic+tag@example.test";
        const string organizationName = "<img src=x onerror=alert(1)> & org";
        const string roleName = "<script>role</script> & reviewer";
        const string url = "https://app.example.test/invite?next=%3Cscript%3E&token=a%26b";
        ServiceCollection services = new();
        services.AddEmailModule(Configuration(), isDevelopment: true);
        using ServiceProvider provider = services.BuildServiceProvider();

        EmailPreparedMessage message = new InvitationEmailRendering(provider.GetRequiredService<BrandedEmailTemplate>())
            .Render(recipient, organizationName, roleName, url);

        message.Recipient.ShouldBe(recipient);
        message.TextBody.ShouldContain(organizationName);
        message.TextBody.ShouldContain(roleName);
        string html = message.HtmlBody.ShouldNotBeNull();
        html.ShouldContain("&lt;img src=x onerror=alert(1)&gt; &amp; org");
        html.ShouldContain("&lt;script&gt;role&lt;/script&gt; &amp; reviewer");
        html.ShouldContain("next=%3Cscript%3E&amp;token=a%26b");
        html.ShouldNotContain("<script>role</script>");
        html.ShouldNotContain("<img src=x");
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Email:Branding:ApplicationName"] = "Kamenta & partners",
        ["Email:Branding:AccentColor"] = "#245a48",
        ["Email:Branding:LogoUrl"] = "https://app.example.test/logo.png?size=32&format=png"
    }).Build();
}
#endif
