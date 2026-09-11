using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using Trykatch.Identity;

namespace Trykatch.IntegrationTests;

[TestClass]
public sealed class ProductionIdentityConfigurationTests
{
    [TestMethod]
    [DataRow("SigningCertificate", "missing-file")]
    [DataRow("SigningCertificate", "wrong-password")]
    [DataRow("SigningCertificate", "no-private-key")]
    [DataRow("SigningCertificate", "expired")]
    [DataRow("EncryptionCertificate", "missing-file")]
    [DataRow("EncryptionCertificate", "wrong-password")]
    [DataRow("EncryptionCertificate", "no-private-key")]
    [DataRow("EncryptionCertificate", "expired")]
    public void OpenIddictUsesTheSameFailClosedCertificatePolicy(string purpose, string problem)
    {
        using IdentityCertificateFixture certificates = new();
        Dictionary<string, string?> settings = certificates.Settings();
        settings["DataProtection:Certificate:Path"] = certificates.Create("active");
        settings["DataProtection:Certificate:Password"] = IdentityCertificateFixture.Password;
        string path = certificates.Create("sensitive-invalid", problem != "no-private-key", problem == "expired");
        if (problem == "missing-file") File.Delete(path);
        settings[$"OpenIddict:{purpose}:Path"] = path;
        if (problem == "wrong-password") settings[$"OpenIddict:{purpose}:Password"] = "sensitive-wrong-password";
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Production });
        builder.Configuration.AddInMemoryCollection(settings);
        OptionsValidationException failure = Should.Throw<OptionsValidationException>(() => builder.Services.AddIdentity(builder.Configuration, false));
        failure.Message.ShouldContain($"OpenIddict:{purpose}");
        failure.ToString().ShouldNotContain("sensitive-");
        failure.ToString().ShouldNotContain(IdentityCertificateFixture.Password);
    }

    [TestMethod]
    [DataRow("missing-file")]
    [DataRow("invalid-file")]
    [DataRow("wrong-password")]
    [DataRow("no-private-key")]
    [DataRow("expired")]
    [DataRow("conflicting-passwords")]
    public async Task ProductionRejectsUnusableCertificateWithoutDisclosingConfiguration(string problem)
    {
        using IdentityCertificateFixture certificates = new();
        Dictionary<string, string?> settings = certificates.Settings();
        string path = certificates.Create("sensitive-certificate-path", problem != "no-private-key", problem == "expired");
        if (problem == "missing-file") File.Delete(path);
        if (problem == "invalid-file") File.WriteAllText(path, "sensitive-invalid-certificate");
        settings["DataProtection:Certificate:Path"] = path;
        settings["DataProtection:Certificate:Password"] = problem == "wrong-password" ? "sensitive-wrong-password" : IdentityCertificateFixture.Password;
        if (problem == "conflicting-passwords") settings["DataProtection:Certificate:PasswordFile"] = path;
        OptionsValidationException failure = await Should.ThrowAsync<OptionsValidationException>(async () =>
        {
            HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Production });
            builder.Configuration.AddInMemoryCollection(settings);
            builder.Services.AddIdentity(builder.Configuration, false);
            using IHost host = builder.Build();
            await host.StartAsync();
        });
        failure.Message.ShouldContain("DataProtection:Certificate");
        failure.ToString().ShouldNotContain("sensitive-");
        failure.ToString().ShouldNotContain(IdentityCertificateFixture.Password);
    }

    [TestMethod]
    public async Task ProductionHostRejectsMissingKeyEncryptionConfigurationBeforeServing()
    {
        using IdentityCertificateFixture certificates = new();
        Dictionary<string, string?> settings = certificates.Settings();
        OptionsValidationException failure = await Should.ThrowAsync<OptionsValidationException>(async () =>
        {
            HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Production });
            builder.Configuration.AddInMemoryCollection(settings);
            builder.Services.AddIdentity(builder.Configuration, isDevelopment: false);
            using IHost host = builder.Build();
            await host.StartAsync();
        });
        failure.Message.ShouldContain("DataProtection:Certificate");
        failure.Message.ShouldNotContain("Password=unused");
    }
}
