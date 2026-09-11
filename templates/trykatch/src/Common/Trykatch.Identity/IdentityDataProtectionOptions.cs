namespace Trykatch.Identity;

public sealed class CertificateOptions
{
    public string? Path { get; set; }
    public string? Password { get; set; }
    public string? PasswordFile { get; set; }
}

public sealed class IdentityDataProtectionOptions
{
    public CertificateOptions Certificate { get; set; } = new();
    public List<CertificateOptions> DecryptionCertificates { get; set; } = [];
}
