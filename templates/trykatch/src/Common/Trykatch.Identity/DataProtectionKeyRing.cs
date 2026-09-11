using System.Security.Cryptography.X509Certificates;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption.ConfigurationModel;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Trykatch.Identity;

internal static class DataProtectionKeyRing
{
    internal static readonly XNamespace EncryptionNamespace = "http://schemas.asp.net/2015/03/dataProtection";
    internal static InvalidOperationException Invalid() => new("The identity key ring is invalid, unencrypted, or missing a decryption certificate. Run the privileged key maintenance preflight.");

    internal static XElement Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml) || xml.Length > 1024 * 1024) throw Invalid();
        using StringReader input = new(xml);
        using XmlReader reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
        return XElement.Load(reader, LoadOptions.PreserveWhitespace);
    }

    internal static HashSet<Guid> Inspect(IEnumerable<XElement> elements, bool requireEncrypted)
    {
        HashSet<Guid> ids = [];
        foreach (XElement element in elements)
        {
            if ((string?)element.Attribute("version") != "1") throw Invalid();
            if (element.Name == "revocation")
            {
                _ = XmlConvert.ToDateTimeOffset((string?)element.Element("revocationDate") ?? "");
                string? revoked = (string?)element.Element("key")?.Attribute("id");
                if (revoked != "*" && !Guid.TryParse(revoked, out _)) throw Invalid();
                continue;
            }
            if (element.Name != "key" || !Guid.TryParse((string?)element.Attribute("id"), out Guid id) || !ids.Add(id)) throw Invalid();
            foreach (string name in new[] { "creationDate", "activationDate", "expirationDate" })
                _ = XmlConvert.ToDateTimeOffset((string?)element.Element(name) ?? "");
            XElement? descriptor = element.Element("descriptor");
            if ((string?)descriptor?.Attribute("deserializerType") != typeof(AuthenticatedEncryptorDescriptorDeserializer).AssemblyQualifiedName) throw Invalid();
            XElement inner = descriptor!.Element("descriptor") ?? throw Invalid();
            XElement[] encrypted = inner.Descendants(EncryptionNamespace + "encryptedSecret").ToArray();
            XElement[] plaintext = inner.Descendants().Where(value => value.Attribute(EncryptionNamespace + "requiresEncryption") is { } marker && XmlConvert.ToBoolean(marker.Value)).ToArray();
            if (inner.Descendants("masterKey").Count() != plaintext.Length) throw Invalid();
            if (encrypted.Length + plaintext.Length != 1 || (requireEncrypted && plaintext.Length != 0)) throw Invalid();
            if (plaintext.Length == 1 && (plaintext[0].Name != "masterKey" || plaintext[0].Parent != inner)) throw Invalid();
            if (encrypted.Length == 1 && (encrypted[0].Parent != inner || encrypted[0].Elements().Count() != 1 ||
                (string?)encrypted[0].Attribute("decryptorType") != typeof(EncryptedXmlDecryptor).AssemblyQualifiedName)) throw Invalid();
        }
        return ids;
    }

    internal static void Verify(IKeyManager manager, IReadOnlySet<Guid> expected)
    {
        IReadOnlyCollection<IKey> keys = manager.GetAllKeys();
        if (!expected.SetEquals(keys.Select(key => key.KeyId))) throw Invalid();
        foreach (IKey key in keys) if (key.CreateEncryptor() is null) throw Invalid();
    }

    internal static void VerifyFresh(XElement[] elements, IReadOnlyCollection<X509Certificate2> certificates, IReadOnlySet<Guid> expected)
    {
        ServiceCollection services = new();
        services.AddLogging(logging => logging.ClearProviders());
        services.AddDataProtection().DisableAutomaticKeyGeneration().UnprotectKeysWithAnyCertificate(certificates.ToArray());
        services.Configure<KeyManagementOptions>(options => options.XmlRepository = new SnapshotRepository(elements));
        using ServiceProvider provider = services.BuildServiceProvider();
        Verify(provider.GetRequiredService<IKeyManager>(), expected);
    }

    private sealed class SnapshotRepository(XElement[] elements) : IXmlRepository
    {
        public IReadOnlyCollection<XElement> GetAllElements() => elements.Select(element => new XElement(element)).ToArray();
        public void StoreElement(XElement element, string friendlyName) => throw new InvalidOperationException("Key verification cannot create keys.");
    }
}

internal sealed class IdentityKeyRingValidation(IServiceProvider services, IdentityCertificates certificates) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = certificates.Active;
        // Startup uses exactly the same guarded repository boundary as every refresh.
        _ = services.GetRequiredService<IKeyManager>().GetAllKeys();
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
