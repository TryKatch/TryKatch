#if TRYKATCH_EMAIL
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trykatch.Application.Identity;
using Trykatch.Application.Mailing;
using Trykatch.Infrastructure.Modules.Email;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class PasswordResetEmailRendererTests
{
    [TestMethod]
    public async Task RendererUsesTheSameBrandedOutputAsTheSynchronousRecoveryNotifier()
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
        const string recipient = "recipient@example.test";
        const string displayName = "Name <script>";
        const string resetUrl = "https://app.example.test/reset/fixture?token=private&next=account";
        PasswordResetEmailIntent intent = new(Guid.NewGuid(), Guid.NewGuid(), recipient, displayName,
            "private-token", resetUrl);

        EmailPreparedMessage prepared = provider.GetRequiredService<IPasswordResetEmailRenderer>().Render(intent);
        await provider.GetRequiredService<IAccountRecoveryNotifier>().SendPasswordResetAsync(recipient, displayName, resetUrl);
        EmailMessage legacy = sender.Message.ShouldNotBeNull();

        prepared.Recipient.ShouldBe(legacy.Recipient);
        prepared.Subject.ShouldBe(legacy.Subject);
        prepared.TextBody.ShouldBe(legacy.TextBody);
        prepared.HtmlBody.ShouldBe(legacy.HtmlBody);
        prepared.Subject.ShouldBe("Reset your Kamenta & partners password");
        prepared.TextBody.ShouldContain(resetUrl);
        string preparedHtml = prepared.HtmlBody.ShouldNotBeNull();
        preparedHtml.ShouldContain("Name &lt;script&gt;");
        preparedHtml.ShouldContain("token=private&amp;next=account");
        sender.SendCount.ShouldBe(1);
    }

    [TestMethod]
    public void EmailMessageToStringDoesNotExposeRecipientOrContent()
    {
        const string sensitive = "reset-token-private@example.test";
        EmailMessage message = new(sensitive, sensitive, sensitive, sensitive);

        message.ToString().ShouldNotContain(sensitive);
    }

    private sealed class RecordingSender : IEmailSender
    {
        public EmailMessage? Message { get; private set; }
        public int SendCount { get; private set; }

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Message = message;
            SendCount++;
            return Task.CompletedTask;
        }
    }
}
#endif
