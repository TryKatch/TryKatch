#if TRYKATCH_EMAIL
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
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
        await scope.ServiceProvider.GetRequiredService<IEmailSender>().SendAsync(new("recipient@example.test", "fixture message", "fixture body"));
        server.ReceivedMessage.ShouldBeTrue();
        server.Authenticated.ShouldBeTrue();
        server.UsedTls.ShouldBe(security != "None");
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
        EmailDeliveryException exception = await Should.ThrowAsync<EmailDeliveryException>(() =>
            scope.ServiceProvider.GetRequiredService<IEmailSender>().SendAsync(new("recipient@example.test", "private subject", "private recovery link")));
        exception.Message.ShouldBe("Email delivery failed.");
        exception.InnerException.ShouldBeNull();
        server.ReceivedMessage.ShouldBeFalse();
        if (failure != "wrong-password") server.Authenticated.ShouldBeFalse();
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
