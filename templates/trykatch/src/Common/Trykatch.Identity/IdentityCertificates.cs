using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;

namespace Trykatch.Identity;

public sealed class OpenIddictCertificateOptions
{
    public CertificateOptions SigningCertificate { get; set; } = new();
    public CertificateOptions EncryptionCertificate { get; set; } = new();
}

internal sealed class IdentityCertificates : IDisposable
{
    public required X509Certificate2 Active { get; init; }
    public required X509Certificate2[] Retired { get; init; }
    public required X509Certificate2 Signing { get; init; }
    public required X509Certificate2 Encryption { get; init; }

    public static IdentityCertificates Load(IConfiguration configuration)
    {
        IdentityDataProtectionOptions protection = configuration.GetSection("DataProtection").Get<IdentityDataProtectionOptions>() ?? new();
        OpenIddictCertificateOptions openIddict = configuration.GetSection("OpenIddict").Get<OpenIddictCertificateOptions>() ?? new();
        List<X509Certificate2> loaded = [];
        X509Certificate2 Load(CertificateOptions options, string setting, bool signing = false, bool retired = false)
        {
            X509Certificate2 certificate = CertificateLoader.Load(options, setting, signing, retired);
            loaded.Add(certificate);
            return certificate;
        }
        try
        {
            return new()
            {
                Active = Load(protection.Certificate, "DataProtection:Certificate"),
                Retired = protection.DecryptionCertificates.Select((item, index) => Load(item, $"DataProtection:DecryptionCertificates:{index}", retired: true)).ToArray(),
                Signing = Load(openIddict.SigningCertificate, "OpenIddict:SigningCertificate", signing: true),
                Encryption = Load(openIddict.EncryptionCertificate, "OpenIddict:EncryptionCertificate")
            };
        }
        catch { foreach (X509Certificate2 certificate in loaded) certificate.Dispose(); throw; }
    }

    public void Dispose()
    {
        Active.Dispose();
        foreach (X509Certificate2 certificate in Retired) certificate.Dispose();
        Signing.Dispose();
        Encryption.Dispose();
    }
}
