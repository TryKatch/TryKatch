using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Trykatch.IntegrationTests;

internal sealed class IdentityCertificateFixture : IDisposable
{
    public const string Password = "Certificate-only!Test-secret-42";
    public string DirectoryPath { get; } = Directory.CreateTempSubdirectory("trykatch-certificates-").FullName;

    public string Create(string name, bool privateKey = true, bool expired = false)
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(expired ? -1 : 5));
        string path = Path.Combine(DirectoryPath, name + ".pfx");
        using X509Certificate2 publicCertificate = X509CertificateLoader.LoadCertificate(certificate.RawData);
        File.WriteAllBytes(path, (privateKey ? certificate : publicCertificate).Export(X509ContentType.Pkcs12, Password));
        return path;
    }

    public Dictionary<string, string?> Settings() => new()
    {
        ["ConnectionStrings:trykatch-identity"] = "Host=localhost;Database=configuration_test;Username=unused;Password=unused",
        ["OpenIddict:SigningCertificate:Path"] = Create("signing"),
        ["OpenIddict:SigningCertificate:Password"] = Password,
        ["OpenIddict:EncryptionCertificate:Path"] = Create("encryption"),
        ["OpenIddict:EncryptionCertificate:Password"] = Password
    };

    public string Reissue(string source, string name, bool omitRsaParameters = false)
    {
        using X509Certificate2 original = X509CertificateLoader.LoadPkcs12FromFile(source, Password, X509KeyStorageFlags.Exportable);
        using RSA key = original.GetRSAPrivateKey()!;
        // NULL and absent RSA AlgorithmIdentifier parameters encode the same key.
        PublicKey publicKey = omitRsaParameters
            ? new PublicKey(original.PublicKey.Oid, null, original.PublicKey.EncodedKeyValue)
            : original.PublicKey;
        X500DistinguishedName subject = new($"CN={name}");
        CertificateRequest request = new(subject, publicKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        using X509Certificate2 reissued = request.Create(subject, X509SignatureGenerator.CreateForRSA(key, RSASignaturePadding.Pkcs1),
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(6), RandomNumberGenerator.GetBytes(16));
        using X509Certificate2 paired = reissued.CopyWithPrivateKey(key);
        string path = Path.Combine(DirectoryPath, name + ".pfx");
        File.WriteAllBytes(path, paired.Export(X509ContentType.Pkcs12, Password));
        return path;
    }

    public string CreateEcSigning()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new("CN=ec-signing", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(5));
        string path = Path.Combine(DirectoryPath, "ec-signing.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12, Password));
        return path;
    }

    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}
