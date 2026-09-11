using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Trykatch.Identity;

public sealed record KeyMaintenanceResult(string Mode, int Keys, int Plaintext, int Updated);

/// <summary>Explicit owner-only maintenance; never invoked by an API replica.</summary>
public static class DataProtectionKeyMaintenance
{
    public static async Task<KeyMaintenanceResult> RunAsync(string connectionString, IConfiguration configuration, bool apply, CancellationToken cancellationToken = default)
    {
        IdentityDataProtectionOptions options = configuration.GetSection("DataProtection").Get<IdentityDataProtectionOptions>() ?? new();
        List<X509Certificate2> certificates = [];
        try
        {
            X509Certificate2 active = CertificateLoader.Load(options.Certificate, "DataProtection:Certificate");
            certificates.Add(active);
            foreach ((CertificateOptions item, int index) in options.DecryptionCertificates.Select((item, index) => (item, index)))
                certificates.Add(CertificateLoader.Load(item, $"DataProtection:DecryptionCertificates:{index}", allowExpired: true));
            CertificateLoader.RequireDistinctKeys(certificates, "DataProtection");
            await using IdentityDbContext database = new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(connectionString).Options);
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            bool owner = await database.Database.SqlQueryRaw<bool>("""
                SELECT (pg_get_userbyid(c.relowner) = current_user OR EXISTS
                    (SELECT 1 FROM pg_roles WHERE rolname = current_user AND rolsuper)) AS "Value"
                FROM pg_class c WHERE c.oid = 'identity.data_protection_keys'::regclass
                """).SingleAsync(cancellationToken);
            if (!owner) throw DataProtectionKeyRing.Invalid();
            // Quiesce API writers operationally too: this lock cannot stop an old
            // replica from creating another plaintext key after maintenance exits.
            await database.Database.ExecuteSqlRawAsync("LOCK TABLE identity.data_protection_keys IN EXCLUSIVE MODE", cancellationToken);
            BoundedDataProtectionKey[] rows = await BoundedDataProtectionKeyQuery.Read(database).ToArrayAsync(cancellationToken);
            XElement[] elements = rows.Select(row => DataProtectionKeyRing.Parse(row.Xml)).ToArray();
            HashSet<Guid> ids = DataProtectionKeyRing.Inspect(elements, requireEncrypted: false);
            DataProtectionKeyRing.VerifyFresh(elements, certificates, ids);
            CertificateXmlEncryptor encryptor = new(active, NullLoggerFactory.Instance);
            int plaintext = 0;
            for (int index = 0; index < elements.Length; index++)
            {
                XElement element = elements[index];
                if (element.Name != "key") continue;
                XElement? secret = element.Descendants().SingleOrDefault(value => value.Attribute(DataProtectionKeyRing.EncryptionNamespace + "requiresEncryption") is { } marker && XmlConvert.ToBoolean(marker.Value));
                if (secret is null) continue; // Retain existing wrappers and their decrypting certificates.
                EncryptedXmlInfo encrypted = encryptor.Encrypt(secret);
                secret.ReplaceWith(new XElement(DataProtectionKeyRing.EncryptionNamespace + "encryptedSecret",
                    new XAttribute("decryptorType", encrypted.DecryptorType.AssemblyQualifiedName!), encrypted.EncryptedElement));
                plaintext++;
                string protectedXml = element.ToString(SaveOptions.DisableFormatting);
                _ = DataProtectionKeyRing.Parse(protectedXml); // Dry-run must reject a wrapper that would exceed the persisted bound too.
                if (apply)
                {
                    DataProtectionKey changed = new() { Id = rows[index].Id, Xml = protectedXml };
                    database.Attach(changed).Property(key => key.Xml).IsModified = true;
                }
            }
            DataProtectionKeyRing.Inspect(elements, requireEncrypted: true);
            DataProtectionKeyRing.VerifyFresh(elements, certificates, ids);
            if (apply)
            {
                await database.SaveChangesAsync(cancellationToken);
                // Re-read persisted XML, with no key-manager cache from the old ring.
                BoundedDataProtectionKey[] written = await BoundedDataProtectionKeyQuery.Read(database).ToArrayAsync(cancellationToken);
                XElement[] persisted = written.Select(key => DataProtectionKeyRing.Parse(key.Xml)).ToArray();
                if (!ids.SetEquals(DataProtectionKeyRing.Inspect(persisted, requireEncrypted: true))) throw DataProtectionKeyRing.Invalid();
                DataProtectionKeyRing.VerifyFresh(persisted, certificates, ids);
            }
            await transaction.CommitAsync(cancellationToken);
            return new(apply ? "apply" : "dry-run", ids.Count, plaintext, apply ? plaintext : 0);
        }
        catch (Exception failure) when (failure is CryptographicException or XmlException or FormatException or InvalidOperationException or NpgsqlException or DbUpdateException)
        {
            throw new InvalidOperationException("Key maintenance failed. Check database ownership, supported key records, and decryption certificates.");
        }
        finally { foreach (X509Certificate2 certificate in certificates) certificate.Dispose(); }
    }

}
