using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;

namespace Trykatch.Identity;

/// <summary>Loads operator-supplied credentials without exposing secret values in errors.</summary>
public static class CertificateLoader
{
    public static X509Certificate2 Load(CertificateOptions options, string setting, bool signing = false, bool allowExpired = false)
    {
        X509Certificate2? certificate = null;
        try
        {
            if (string.IsNullOrWhiteSpace(options.Path)) throw Invalid(setting, "Path is required.");
            string password = Password(options, setting);
            FileInfo file = new(options.Path);
            if (!file.Exists || file.Length is 0 or > 4 * 1024 * 1024) throw Invalid(setting, "The certificate file is missing, empty, or too large.");
            // macOS requires a temporary keychain; Linux/Windows imports stay in memory.
            X509KeyStorageFlags flags = OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
            certificate = X509CertificateLoader.LoadPkcs12FromFile(options.Path, password, flags);
            if (!certificate.HasPrivateKey) throw Invalid(setting, "A usable private key is required.");
            if (!allowExpired && (certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow))
                throw Invalid(setting, "The active certificate must be currently valid.");
            X509KeyUsageFlags required = signing ? X509KeyUsageFlags.DigitalSignature : X509KeyUsageFlags.KeyEncipherment;
            if (certificate.Extensions.OfType<X509KeyUsageExtension>().Any(usage => (usage.KeyUsages & required) == 0))
                throw Invalid(setting, "The certificate key usage does not support this purpose.");
            byte[] challenge = RandomNumberGenerator.GetBytes(32);
            using RSA? rsa = certificate.GetRSAPrivateKey();
            if (rsa is not null)
            {
                if (rsa.KeySize < 2048) throw Invalid(setting, "RSA keys must have at least 2048 bits.");
                bool works = signing
                    ? rsa.VerifyData(challenge, rsa.SignData(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                    : CryptographicOperations.FixedTimeEquals(challenge, rsa.Decrypt(rsa.Encrypt(challenge, RSAEncryptionPadding.OaepSHA1), RSAEncryptionPadding.OaepSHA1));
                if (!works) throw Invalid(setting, "The private key failed its cryptographic preflight.");
            }
            else
            {
                using ECDsa? ec = signing ? certificate.GetECDsaPrivateKey() : null;
                if (ec is null || ec.KeySize < 256 || !ec.VerifyData(challenge, ec.SignData(challenge, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256))
                    throw Invalid(setting, "The certificate uses an unsupported key for this purpose.");
            }
            return certificate;
        }
        catch (Exception failure) when (failure is CryptographicException or IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException or OptionsValidationException)
        {
            certificate?.Dispose();
            if (failure is OptionsValidationException) throw;
            // No inner exception: crypto/filesystem messages can contain paths or secrets.
            throw Invalid(setting, "The certificate or its password could not be loaded.");
        }
    }

    private static string Password(CertificateOptions options, string setting)
    {
        if (!string.IsNullOrEmpty(options.Password) && !string.IsNullOrEmpty(options.PasswordFile))
            throw Invalid(setting, "Configure Password or PasswordFile, not both.");
        string? password = options.Password;
        if (!string.IsNullOrEmpty(options.PasswordFile))
        {
            FileInfo file = new(options.PasswordFile);
            if (!file.Exists || file.Length is 0 or > 4096) throw Invalid(setting, "PasswordFile is missing, empty, or too large.");
            password = File.ReadAllText(file.FullName).TrimEnd('\r', '\n');
        }
        if (string.IsNullOrWhiteSpace(password)) throw Invalid(setting, "Password or PasswordFile is required.");
        return password;
    }

    internal static OptionsValidationException Invalid(string setting, string reason) =>
        new(setting, typeof(CertificateOptions), [$"{setting}: {reason}"]);
}
