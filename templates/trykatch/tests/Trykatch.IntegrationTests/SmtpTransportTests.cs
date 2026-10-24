#if TRYKATCH_EMAIL
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MimeKit;
using Shouldly;
using Trykatch.Application.Identity;
using Trykatch.Infrastructure.Modules.Email;

namespace Trykatch.IntegrationTests;

[TestClass]
public sealed class SmtpTransportTests
{
    [TestMethod]
    [DataRow("StartTls")]
    [DataRow("SslOnConnect")]
    [DataRow("None")]
    public async Task RealSmtpDeliveryUsesConfiguredTransport(string security)
    {
        await using LoopbackSmtpServer server = new(implicitTls: security == "SslOnConnect");
        using IHost host = CreateHost(server, security, development: security == "None");
        await host.StartAsync();
        using IServiceScope scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IInvitationNotifier>().SendOrganizationInvitationAsync(
            "recipient@example.test", "Fixture workspace", "Viewer", "https://app.example.test/invite/fixture");
        server.ReceivedMessage.ShouldBeTrue();
        server.Authenticated.ShouldBeTrue();
        server.UsedTls.ShouldBe(security != "None");
        using MemoryStream content = new(System.Text.Encoding.UTF8.GetBytes(server.MessageData.ShouldNotBeNull()));
        using MimeMessage message = MimeMessage.Load(content);
        message.TextBody.ShouldNotBeNull().ShouldContain("Workspace role: Viewer");
        message.HtmlBody.ShouldNotBeNull().ShouldContain("Workspace role: <strong>Viewer</strong>");
    }

    [TestMethod]
    [DataRow("missing-starttls")]
    [DataRow("wrong-password")]
    [DataRow("untrusted-certificate")]
    [DataRow("wrong-hostname")]
    [DataRow("expired-certificate")]
    public async Task RealSmtpFailsClosedWithoutDeliveryOrCredentialDisclosure(string failure)
    {
        await using LoopbackSmtpServer server = new(advertiseStartTls: failure != "missing-starttls", wrongName: failure == "wrong-hostname", expired: failure == "expired-certificate");
        using IHost host = CreateHost(server, "StartTls", wrongPassword: failure == "wrong-password", trustFixture: failure != "untrusted-certificate");
        await host.StartAsync();
        using IServiceScope scope = host.Services.CreateScope();
        if (failure == "missing-starttls")
        {
            EmailTransportResult result = await scope.ServiceProvider.GetRequiredService<IEmailTransport>().SendAsync(
                new("recipient@example.test", "private subject", "private recovery link"));
            result.Disposition.ShouldBe(EmailTransportDisposition.PermanentFailure);
            result.Stage.ShouldBe(EmailTransportStage.Connect);
            result.FailureCode.ShouldBe(EmailTransportFailureCode.ProtocolFailure);
        }
        else
        {
            EmailDeliveryException exception = await Should.ThrowAsync<EmailDeliveryException>(() =>
                scope.ServiceProvider.GetRequiredService<IEmailSender>().SendAsync(new("recipient@example.test", "private subject", "private recovery link")));
            exception.Message.ShouldBe("Email delivery failed.");
            exception.InnerException.ShouldBeNull();
        }
        server.ReceivedMessage.ShouldBeFalse();
        if (failure != "wrong-password") server.Authenticated.ShouldBeFalse();
    }

    [TestMethod]
    public async Task AcceptedMessageRemainsAcknowledgedWhenConnectionResetsDuringCleanup()
    {
        await using LoopbackSmtpServer server = new(resetAfterDataAcknowledgement: true);
        using IHost host = CreateHost(server, "None", development: true);
        await host.StartAsync();
        using IServiceScope scope = host.Services.CreateScope();

        EmailTransportResult result = await scope.ServiceProvider.GetRequiredService<IEmailTransport>().SendAsync(
            new("recipient@example.test", "private subject", "private body"));
        result.Disposition.ShouldBe(EmailTransportDisposition.Acknowledged);
        result.Stage.ShouldBe(EmailTransportStage.None);
        result.FailureCode.ShouldBe(EmailTransportFailureCode.None);
        server.ReceivedMessage.ShouldBeTrue();
    }

    [TestMethod]
    public async Task CancellationDuringQuitCleanupReturnsAcknowledgedWithinAttemptBudget()
    {
        await using LoopbackSmtpServer server = new(holdQuitResponse: true);
        using IHost host = CreateHost(server, "None", development: true);
        await host.StartAsync();
        using IServiceScope scope = host.Services.CreateScope();
        using CancellationTokenSource cancellation = new();

        Task<EmailTransportResult> sending = scope.ServiceProvider.GetRequiredService<IEmailTransport>().SendAsync(
            new("recipient@example.test", "private subject", "private body"), cancellation.Token);
        await server.QuitReceived.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        EmailTransportResult result = await sending.WaitAsync(TimeSpan.FromSeconds(1));
        result.Disposition.ShouldBe(EmailTransportDisposition.Acknowledged);
        server.ReceivedMessage.ShouldBeTrue();
    }

    [TestMethod]
    [DataRow(450, true)]
    [DataRow(550, false)]
    public async Task RecipientRejectionMapsSmtpStatusToBoundedOutcome(int status, bool retryable)
    {
        await using LoopbackSmtpServer server = new(recipientStatusCode: status);
        using IHost host = CreateHost(server, "None", development: true);
        await host.StartAsync();
        using IServiceScope scope = host.Services.CreateScope();

        EmailTransportResult result = await scope.ServiceProvider.GetRequiredService<IEmailTransport>().SendAsync(
            new("recipient@example.test", "private subject", "private body"));

        result.Disposition.ShouldBe(retryable ? EmailTransportDisposition.RetryableFailure : EmailTransportDisposition.PermanentFailure);
        result.Stage.ShouldBe(EmailTransportStage.Recipient);
        result.SmtpStatusCode.ShouldBe(status);
        result.FailureCode.ShouldBe(EmailTransportFailureCode.SmtpRejected);
        result.ToString().ShouldNotContain("recipient@example.test");
        result.ToString().ShouldNotContain("private body");
        server.ReceivedMessage.ShouldBeFalse();
    }

    [TestMethod]
    public async Task AcceptedDataWithoutFinalAcknowledgementIsUncertain()
    {
        await using LoopbackSmtpServer server = new(dropFinalAcknowledgement: true);
        using IHost host = CreateHost(server, "None", development: true);
        await host.StartAsync();
        using IServiceScope scope = host.Services.CreateScope();

        EmailTransportResult result = await scope.ServiceProvider.GetRequiredService<IEmailTransport>().SendAsync(
            new("recipient@example.test", "private subject", "private body"));

        result.Disposition.ShouldBe(EmailTransportDisposition.Uncertain);
        result.Stage.ShouldBe(EmailTransportStage.Send);
        result.FailureCode.ShouldBe(EmailTransportFailureCode.ProtocolFailure);
        result.SmtpStatusCode.ShouldBeNull();
        result.ToString().ShouldNotContain("recipient@example.test");
        result.ToString().ShouldNotContain("private body");
        server.ReceivedMessage.ShouldBeTrue();
    }

    [TestMethod]
    public async Task CancellationBeforeSendPropagatesWithoutConnecting()
    {
        await using LoopbackSmtpServer server = new();
        using IHost host = CreateHost(server, "None", development: true);
        await host.StartAsync();
        using IServiceScope scope = host.Services.CreateScope();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            scope.ServiceProvider.GetRequiredService<IEmailTransport>().SendAsync(
                new("recipient@example.test", "private subject", "private body"), cancellation.Token));
        server.ReceivedMessage.ShouldBeFalse();
    }

    [TestMethod]
    [DataRow("missing-security")]
    [DataRow("unknown-security")]
    [DataRow("numeric-security")]
    [DataRow("missing-host")]
    [DataRow("bad-port")]
    [DataRow("bad-from")]
    [DataRow("unpaired-credentials")]
    [DataRow("missing-password-file")]
    public async Task ProductionRejectsInvalidOptionsWithSafeError(string problem)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Production });
        Dictionary<string, string?> settings = new() { ["Email:Host"] = "localhost", ["Email:From"] = "sender@example.test", ["Email:Security"] = "StartTls" };
        settings[problem switch
        {
            "missing-security" or "unknown-security" or "numeric-security" => "Email:Security",
            "missing-host" => "Email:Host",
            "bad-port" => "Email:Port",
            "bad-from" => "Email:From",
            "unpaired-credentials" => "Email:Username",
            _ => "Email:PasswordFile"
        }] = problem switch { "missing-security" or "missing-host" => null, "numeric-security" => "999", "bad-port" => "0", _ => "sensitive-invalid-value" };
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddEmailModule(builder.Configuration);
        using IHost host = builder.Build();
        OptionsValidationException exception = await Should.ThrowAsync<OptionsValidationException>(() => host.StartAsync());
        exception.ToString().ShouldNotContain("sensitive-invalid-value");
    }

    private static IHost CreateHost(LoopbackSmtpServer server, string security, bool development = false, bool wrongPassword = false, bool trustFixture = true)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = development ? Environments.Development : Environments.Production });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Email:Host"] = "localhost",
            ["Email:Port"] = server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Email:From"] = "sender@example.test",
            ["Email:Security"] = security,
            ["Email:Username"] = "fixture-user",
            ["Email:Password"] = wrongPassword ? "sensitive-wrong-password" : "fixture-password"
        });
        if (trustFixture) builder.Services.AddSingleton(server.TrustedClientFactory());
        builder.Services.AddEmailModule(builder.Configuration, development);
        return builder.Build();
    }

    [TestMethod]
    public async Task ProductionHostRejectsPlaintextSmtpBeforeServing()
    {
        await Should.ThrowAsync<OptionsValidationException>(async () =>
        {
            HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Production });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:Host"] = "localhost",
                ["Email:Port"] = "1025",
                ["Email:From"] = "sender@example.test",
                ["Email:Security"] = "None"
            });
            builder.Services.AddEmailModule(builder.Configuration);
            using IHost host = builder.Build();
            await host.StartAsync();
        });
    }
}
#endif
