using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Trykatch.ModuleTool;

namespace Trykatch.UnitTests;

internal static class SignedModuleTestArtifacts
{
    private static readonly string[] Creators = ["Tool: Trykatch test fixture"];
    public static void Create(
        string root,
        string moduleId,
        string version,
        string? invalidEvidence = null,
        string dotnetPackageId = "Trykatch.Modules.Reporting",
        string frontendPackageId = "@trykatch/module-reporting",
        string? backendPackageSource = null)
    {
        string backend = $"{moduleId}.{version}.nupkg";
        string frontend = $"{moduleId}.{version}.tgz";
        string provenance = $"{moduleId}.{version}.provenance.json";
        string sbom = $"{moduleId}.{version}.spdx.json";
        using RSA key = RSA.Create(3072);
        CertificateRequest request = new("CN=Trykatch package security test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.3") }, true));
        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(5));
        string fingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        string pfx = Path.Combine(root, $"fixture-{Guid.NewGuid():N}.pfx");
        string password = Guid.NewGuid().ToString("N");
        File.WriteAllBytes(pfx, certificate.Export(X509ContentType.Pfx, password));
        if (backendPackageSource is not null)
        {
            File.Copy(backendPackageSource, Path.Combine(root, backend), overwrite: true);
        }
        else
        {
            using ZipArchive archive = ZipFile.Open(Path.Combine(root, backend), ZipArchiveMode.Create);
            WriteEntry(archive, $"{dotnetPackageId}.nuspec", $"""
                    <?xml version="1.0" encoding="utf-8"?>
                    <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata>
                    <id>{dotnetPackageId}</id><version>{version}</version><authors>Trykatch Tests</authors><description>Signed fixture</description>
                    </metadata></package>
                    """);
            WriteEntry(archive, "README.md", "Signed package test fixture.");
            WriteEntry(archive, "[Content_Types].xml", """
                    <?xml version="1.0" encoding="utf-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="nuspec" ContentType="application/octet"/><Default Extension="md" ContentType="application/octet"/></Types>
                    """);
        }
        try
        {
            WorkspaceCommandResult signed = new ProcessWorkspaceCommandRunner().Run("dotnet",
                ["nuget", "sign", Path.Combine(root, backend), "--certificate-path", pfx, "--certificate-password", password], root);
            if (signed.ExitCode != 0) throw new InvalidOperationException($"Could not create signed package fixture: {signed.Output}");
        }
        finally { File.Delete(pfx); }

        using (FileStream file = File.Create(Path.Combine(root, frontend)))
        using (GZipStream gzip = new(file, CompressionLevel.SmallestSize))
        using (TarWriter archive = new(gzip))
        using (MemoryStream metadata = new(JsonSerializer.SerializeToUtf8Bytes(new { name = frontendPackageId, version = invalidEvidence == "frontend-version" ? "9.9.9" : version })))
            archive.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "package/package.json") { DataStream = metadata });

        (string Purl, string Name, string File)[] artifacts =
        [
            ($"pkg:nuget/{dotnetPackageId}@{version}", dotnetPackageId, backend),
            ($"pkg:npm/{frontendPackageId.Replace("@", "%40", StringComparison.Ordinal)}@{version}", frontendPackageId, frontend)
        ];
        File.WriteAllBytes(Path.Combine(root, sbom), JsonSerializer.SerializeToUtf8Bytes(new
        {
            spdxVersion = "SPDX-2.3", SPDXID = "SPDXRef-DOCUMENT", dataLicense = "CC0-1.0",
            documentNamespace = "https://build.trykatch.test/spdx/" + Guid.NewGuid(),
            creationInfo = new { created = DateTimeOffset.UtcNow, creators = Creators },
            packages = artifacts.Select((artifact, index) => new
            {
                SPDXID = $"SPDXRef-Package{index}", name = artifact.Name, versionInfo = version,
                checksums = new[] { new { algorithm = "SHA256", checksumValue = Hash(root, artifact.File) } },
                externalRefs = new[] { new { referenceCategory = "PACKAGE-MANAGER", referenceType = "purl", referenceLocator = artifact.Purl } }
            }),
            relationships = artifacts.Select((_, index) => new { spdxElementId = "SPDXRef-DOCUMENT", relationshipType = "DESCRIBES", relatedSpdxElement = $"SPDXRef-Package{index}" })
        }));
        if (invalidEvidence == "sbom") File.WriteAllText(Path.Combine(root, sbom), "{}");
        const string builder = "https://build.trykatch.test/fixtures";
        JsonObject statement = new()
        {
            ["_type"] = "https://in-toto.io/Statement/v1",
            ["predicateType"] = "https://slsa.dev/provenance/v1",
            ["subject"] = JsonSerializer.SerializeToNode(artifacts.Select(artifact => new { name = artifact.Purl, digest = new { sha256 = Hash(root, artifact.File) } })
                .Append(new { name = "sbom", digest = new { sha256 = Hash(root, sbom) } })),
            ["predicate"] = JsonSerializer.SerializeToNode(new
            {
                buildDefinition = new { buildType = "https://build.trykatch.test/module/v1", externalParameters = new { securityScan = new { status = "passed", high = 0, critical = 0 } } },
                runDetails = new { builder = new { id = builder }, metadata = new { finishedOn = DateTimeOffset.UtcNow } }
            })
        };
        if (invalidEvidence == "subject") statement["subject"]![0]!["digest"]!["sha256"] = new string('0', 64);
        if (invalidEvidence == "builder") statement["predicate"]!["runDetails"]!["builder"]!["id"] = "https://untrusted.example/build";
        if (invalidEvidence == "stale") statement["predicate"]!["runDetails"]!["metadata"]!["finishedOn"] = DateTimeOffset.UtcNow.AddDays(-60);
        if (invalidEvidence == "vulnerable") statement["predicate"]!["buildDefinition"]!["externalParameters"]!["securityScan"]!["high"] = 1;
        byte[] statementBytes = Encoding.UTF8.GetBytes(statement.ToJsonString());
        File.WriteAllBytes(Path.Combine(root, provenance), statementBytes);
        byte[] signature = key.SignData(statementBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        if (invalidEvidence == "signature") signature[0] ^= 1;
        File.WriteAllText(Path.Combine(root, $"{moduleId}.{version}.provenance.sig"), Convert.ToBase64String(signature));
        JsonNode catalog = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "try" + "katch.modules.json")))!;
        catalog["publisherTrust"] = new JsonObject
        {
            ["trykatch"] = JsonSerializer.SerializeToNode(new
            {
                nugetSignerSha256 = new[] { invalidEvidence == "signer" ? new string('0', 64) : fingerprint }, attestationPublicKey = key.ExportSubjectPublicKeyInfoPem(), builderId = builder, maximumAttestationAgeDays = 7
            })
        };
        File.WriteAllText(Path.Combine(root, "try" + "katch.modules.json"), catalog.ToJsonString());
        File.WriteAllText(Path.Combine(root, "NuGet.Config"), $"""
            <configuration>
              <packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources>
              <config><add key="signatureValidationMode" value="require" /></config>
              <trustedSigners><author name="trykatch"><certificate fingerprint="{fingerprint}" hashAlgorithm="SHA256" allowUntrustedRoot="true" /></author></trustedSigners>
              <packageSourceMapping><packageSource key="nuget.org"><package pattern="*" /></packageSource></packageSourceMapping>
            </configuration>
            """);
    }

    private static void WriteEntry(ZipArchive archive, string name, string value)
    {
        using StreamWriter writer = new(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(value);
    }
    private static string Hash(string root, string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, file)))).ToLowerInvariant();
}
