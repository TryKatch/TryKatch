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

    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}
