using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Trykatch.Identity;

namespace Trykatch.IntegrationTests;

[TestClass]
public sealed class ProductionKeyRepositoryTests
{
    [TestMethod]
    [DataRow("repository-after")]
    [DataRow("post-repository-before")]
    [DataRow("post-repository-after")]
    [DataRow("encryption-after")]
    public void ProductionRejectsOptionsThatBypassItsKeyRepositoryOrEncryption(string registration)
    {
        using IdentityCertificateFixture certificates = new();
        Dictionary<string, string?> settings = certificates.Settings();
        settings["DataProtection:Certificate:Path"] = certificates.Create("active");
        settings["DataProtection:Certificate:Password"] = IdentityCertificateFixture.Password;
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        FileSystemXmlRepository other = new(new DirectoryInfo(certificates.DirectoryPath), NullLoggerFactory.Instance);
        if (registration == "post-repository-before") builder.Services.PostConfigure<KeyManagementOptions>(options => options.XmlRepository = other);
        builder.Services.AddIdentity(builder.Configuration, false);
        if (registration == "repository-after") builder.Services.Configure<KeyManagementOptions>(options => options.XmlRepository = other);
        if (registration == "post-repository-after") builder.Services.PostConfigure<KeyManagementOptions>(options => options.XmlRepository = other);
        if (registration == "encryption-after") builder.Services.Configure<KeyManagementOptions>(options => options.XmlEncryptor = null);
        using IHost host = builder.Build();
        OptionsValidationException failure = Should.Throw<OptionsValidationException>(() => host.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value);
        failure.Message.ShouldContain("DataProtection");
        failure.Message.ShouldNotContain(certificates.DirectoryPath);
    }
}
