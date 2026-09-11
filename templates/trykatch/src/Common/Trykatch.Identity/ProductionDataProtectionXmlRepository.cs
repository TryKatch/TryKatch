using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Trykatch.Identity;

internal sealed class ProductionDataProtectionXmlRepository(
    IServiceProvider services, ILoggerFactory loggerFactory, IdentityCertificates certificates) : IXmlRepository
{
    private readonly EntityFrameworkCoreXmlRepository<IdentityDbContext> persistence = new(services, loggerFactory);

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        // IXmlRepository is synchronous. Read once, before the EF repository's default
        // parser/logging, so the bounded parser and policy guard the exact returned XML.
        using IServiceScope scope = services.CreateScope();
        IdentityDbContext database = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        try
        {
            BoundedDataProtectionKey[] records = BoundedDataProtectionKeyQuery.Read(database).ToArray();
            XElement[] snapshot = records.Select(key => DataProtectionKeyRing.Parse(key.Xml)).ToArray();
            Validate(snapshot);
            return snapshot;
        }
        catch (Exception failure) when (failure is CryptographicException or XmlException or FormatException or InvalidOperationException)
        {
            throw DataProtectionKeyRing.Invalid();
        }
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        try
        {
            XElement snapshot = DataProtectionKeyRing.Parse(element.ToString(SaveOptions.DisableFormatting));
            Validate([snapshot]);
            // Retain the framework's normal scoped EF persistence and certificate wrapper.
            persistence.StoreElement(snapshot, friendlyName);
        }
        catch (Exception failure) when (failure is CryptographicException or XmlException or FormatException or InvalidOperationException)
        {
            throw DataProtectionKeyRing.Invalid();
        }
    }

    private void Validate(XElement[] snapshot)
    {
        HashSet<Guid> expected = DataProtectionKeyRing.Inspect(snapshot, requireEncrypted: true);
        // XmlKeyManager caches by key ID. A fresh verifier must inspect even a replacement
        // retaining that ID, without another database query or any host key-manager cache.
        DataProtectionKeyRing.VerifyFresh(snapshot, [certificates.Active, .. certificates.Retired], expected);
    }
}

internal sealed class ProductionKeyManagementPolicy(ProductionDataProtectionXmlRepository repository, CertificateXmlEncryptor encryptor)
    : IValidateOptions<KeyManagementOptions>
{
    public ValidateOptionsResult Validate(string? name, KeyManagementOptions options) =>
        ReferenceEquals(options.XmlRepository, repository) && ReferenceEquals(options.XmlEncryptor, encryptor)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("DataProtection repository and certificate encryption are production-owned and cannot be overridden.");
}
