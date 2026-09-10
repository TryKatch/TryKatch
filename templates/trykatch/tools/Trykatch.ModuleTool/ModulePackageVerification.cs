using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Trykatch.ModuleTool;

public sealed partial class ModuleWorkspace
{
    private const int MaximumArtifactBytes = 128 * 1024 * 1024;

    private Dictionary<string, byte[]> VerifyPackageArtifacts(ModuleCatalogFile catalog, CandidatePackage candidate)
    {
        ModuleManifest manifest = candidate.Manifest;
        if (!StableIdRegex().IsMatch(manifest.Id) || !TryParseVersion(manifest.Version, out _))
            throw new InvalidOperationException("Package module identity/version is invalid.");
        ModulePackageIdentity backend = manifest.Distribution.Dotnet
            ?? throw new InvalidOperationException("A signed backend package is required.");
        if (backend.Id.Length is < 1 or > 100 || backend.Id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
            throw new InvalidOperationException("Backend package ID is invalid.");
        ModuleSupplyChain supply = manifest.Distribution.SupplyChain
            ?? throw new InvalidOperationException("Signed provenance and an SPDX SBOM are required.");
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        byte[] backendBytes = ReadArtifact(backend.PackageFile, backend.Sha256);
        VerifyArchiveIdentity(backendBytes, backend, manifest.Version, npm: false);
        List<(string Subject, string Digest)> subjects = [(PackageSubject(backend, npm: false), backend.Sha256!)];
        if (manifest.Capabilities.Contains("web", StringComparer.Ordinal) != (manifest.Distribution.Web is not null))
            throw new InvalidOperationException("Web capability and frontend artifact must agree.");
        if (manifest.Distribution.Web is ModulePackageIdentity frontend)
        {
            VerifyArchiveIdentity(ReadArtifact(frontend.PackageFile, frontend.Sha256), frontend, manifest.Version, npm: true);
            if (manifest.Entrypoints.Web.Specifier != frontend.Id)
                throw new InvalidOperationException("Frontend entrypoint must name the verified package.");
            subjects.Add((PackageSubject(frontend, npm: true), frontend.Sha256!));
        }
        byte[] provenance = ReadArtifact(supply.ProvenanceFile, supply.ProvenanceSha256);
        byte[] sbom = ReadArtifact(supply.SbomFile, supply.SbomSha256);
        byte[] signature = ReadArtifact(supply.ProvenanceSignatureFile, null, requireDigest: false);
        if (!catalog.TrustedPublishers.Contains(manifest.Publisher, StringComparer.Ordinal)
            || !catalog.PublisherTrust.TryGetValue(manifest.Publisher, out ModulePublisherTrust? trust) || trust is null)
            throw new InvalidOperationException("Publisher has no explicitly enrolled signing identity.");
        if (trust.NugetSignerSha256.Count == 0 || trust.NugetSignerSha256.Any(value => !IsSha256(value))
            || !Uri.TryCreate(trust.BuilderId, UriKind.Absolute, out _)
            || trust.MaximumAttestationAgeDays is < 1 or > 30)
            throw new InvalidOperationException("Publisher trust configuration is invalid.");
        try
        {
            using RSA key = RSA.Create();
            if (!trust.AttestationPublicKey.StartsWith("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal))
                throw new InvalidOperationException("Publisher attestation key must be an enrolled public key.");
            key.ImportFromPem(trust.AttestationPublicKey);
            if (key.KeySize < 3072 || !key.VerifyData(provenance, Convert.FromBase64String(Encoding.UTF8.GetString(signature).Trim()),
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new InvalidOperationException("Provenance signature does not match the enrolled publisher.");
            ValidateAttestation(provenance, sbom, subjects, supply.SbomSha256, trust);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidOperationException("Signed provenance/SBOM validation failed; the workspace was not changed.", exception);
        }

        string configPath = ResolveInsideRoot("NuGet.Config");
        XDocument config = ReadXml(File.ReadAllBytes(configPath));
        if (!config.Descendants("add").Any(item => (string?)item.Attribute("key") == "signatureValidationMode"
                && (string?)item.Attribute("value") == "require")
            || !config.Descendants("trustedSigners").Elements().Any() || !config.Descendants("packageSourceMapping").Elements().Any())
            throw new InvalidOperationException("NuGet.Config must require signatures and define trusted signers/source mapping.");

        DirectoryInfo staging = Directory.CreateTempSubdirectory("trykatch-verify-");
        try
        {
            string packagePath = Path.Combine(staging.FullName, "verified.nupkg");
            File.WriteAllBytes(packagePath, backendBytes);
            string verificationConfig = Path.Combine(staging.FullName, "NuGet.Config");
            config.Save(verificationConfig);
            List<string> arguments = ["nuget", "verify", packagePath, "--all", "--configfile", verificationConfig];
            foreach (string fingerprint in trust.NugetSignerSha256)
                arguments.AddRange(["--certificate-fingerprint", fingerprint]);
            WorkspaceCommandResult verification = _commandRunner.Run("dotnet", arguments, _root);
            if (verification.ExitCode != 0)
                throw new InvalidOperationException($"NuGet signer verification failed; the workspace was not changed:{Environment.NewLine}{verification.Output}");
        }
        finally { staging.Delete(recursive: true); }
        return files;

        byte[] ReadArtifact(string? relativePath, string? digest, bool requireDigest = true)
        {
            string absolute = ResolvePackageArtifact(candidate.ManifestPath, relativePath, "artifact");
            if (new FileInfo(absolute).Length > MaximumArtifactBytes)
                throw new InvalidOperationException("Module artifact exceeds the verification size limit.");
            string normalized = Path.GetRelativePath(Path.GetDirectoryName(candidate.ManifestPath)!, absolute);
            for (FileSystemInfo? item = new FileInfo(absolute); item is not null
                && item.FullName != Path.GetDirectoryName(candidate.ManifestPath); item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
                if (item.LinkTarget is not null) throw new InvalidOperationException("Module artifacts must not traverse symbolic links.");
            byte[] bytes = File.ReadAllBytes(absolute);
            if (requireDigest && (!IsSha256(digest) || !string.Equals(Hash(bytes), digest, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Module artifact integrity check failed.");
            if (!files.TryAdd(normalized, bytes)) throw new InvalidOperationException("Module artifact paths must be distinct.");
            return bytes;
        }
    }

    private static void VerifyArchiveIdentity(byte[] bytes, ModulePackageIdentity expected, string moduleVersion, bool npm)
    {
        if (expected.Version != moduleVersion || string.IsNullOrWhiteSpace(expected.Id))
            throw new InvalidOperationException("Module and artifact identity/version must agree.");
        try
        {
            using MemoryStream input = new(bytes, writable: false);
            if (!npm)
            {
                using ZipArchive archive = new(input, ZipArchiveMode.Read);
                ZipArchiveEntry[] nuspecs = archive.Entries.Where(entry => !entry.FullName.Contains('/') && entry.Name.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (nuspecs.Length != 1 || nuspecs[0].Length > 1024 * 1024)
                    throw new InvalidOperationException("Backend archive must contain exactly one bounded root nuspec.");
                if (archive.Entries.Select(entry => entry.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != archive.Entries.Count)
                    throw new InvalidOperationException("Duplicate archive entries are forbidden.");
                if (archive.Entries.Any(entry => entry.FullName.StartsWith('/') || entry.FullName.Contains('\\')
                    || entry.FullName.Split('/').Contains("..")) || archive.Entries.Sum(entry => entry.Length) > MaximumArtifactBytes)
                    throw new InvalidOperationException("Unsafe or excessively expanded backend archive.");
                using Stream entry = nuspecs[0].Open();
                using MemoryStream metadata = new();
                entry.CopyTo(metadata);
                XElement root = ReadXml(metadata.ToArray()).Root ?? throw new InvalidOperationException("Missing nuspec.");
                XElement packageMetadata = root.Elements().Single(item => item.Name.LocalName == "metadata");
                string id = packageMetadata.Elements().Single(item => item.Name.LocalName == "id").Value;
                string version = packageMetadata.Elements().Single(item => item.Name.LocalName == "version").Value;
                if (!string.Equals(id, expected.Id, StringComparison.OrdinalIgnoreCase) || version != expected.Version)
                    throw new InvalidOperationException("Backend archive ID/version does not match the reviewed manifest.");
            }
            else
            {
                using GZipStream gzip = new(input, CompressionMode.Decompress);
                using TarReader archive = new(gzip);
                JsonDocument? metadata = null;
                long expandedBytes = 0;
                try
                {
                    TarEntry? entry;
                    while ((entry = archive.GetNextEntry()) is not null)
                    {
                        expandedBytes = checked(expandedBytes + entry.Length);
                        if (expandedBytes > MaximumArtifactBytes || entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink
                            || entry.Name.Split('/').Contains("..") || entry.Name.StartsWith('/') || entry.Name.Contains('\\'))
                            throw new InvalidOperationException("Unsafe frontend archive entry.");
                        if (entry.Name != "package/package.json") continue;
                        if (metadata is not null || entry.Length > 1024 * 1024 || entry.DataStream is null)
                            throw new InvalidOperationException("Frontend archive has invalid package metadata.");
                        metadata = JsonDocument.Parse(entry.DataStream);
                        RequireUniqueProperties(metadata.RootElement);
                    }
                    if (metadata is null || metadata.RootElement.GetProperty("name").GetString() != expected.Id
                        || metadata.RootElement.GetProperty("version").GetString() != expected.Version)
                        throw new InvalidOperationException("Frontend archive ID/version does not match the reviewed manifest.");
                }
                finally { metadata?.Dispose(); }
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or XmlException or JsonException or KeyNotFoundException)
        {
            throw new InvalidOperationException("Module archive is invalid.", exception);
        }
    }

    private static void ValidateAttestation(byte[] provenance, byte[] sbom, List<(string Subject, string Digest)> artifacts,
        string sbomDigest, ModulePublisherTrust trust)
    {
        using JsonDocument statement = JsonDocument.Parse(provenance);
        JsonElement root = statement.RootElement;
        RequireUniqueProperties(root);
        if (root.GetProperty("_type").GetString() != "https://in-toto.io/Statement/v1"
            || root.GetProperty("predicateType").GetString() != "https://slsa.dev/provenance/v1")
            throw new InvalidOperationException("Unsupported signed provenance format.");
        Dictionary<string, string> subjects = root.GetProperty("subject").EnumerateArray().ToDictionary(
            item => item.GetProperty("name").GetString()!, item => item.GetProperty("digest").GetProperty("sha256").GetString()!, StringComparer.Ordinal);
        if (subjects.Count != artifacts.Count + 1 || !subjects.TryGetValue("sbom", out string? recordedSbom) || recordedSbom != sbomDigest)
            throw new InvalidOperationException("Signed subjects must describe exactly the reviewed artifacts and SBOM.");
        foreach ((string name, string digest) in artifacts)
            if (!subjects.TryGetValue(name, out string? actual) || actual != digest)
                throw new InvalidOperationException("Signed artifact subject/digest mismatch.");
        JsonElement predicate = root.GetProperty("predicate");
        JsonElement run = predicate.GetProperty("runDetails");
        DateTimeOffset finished = run.GetProperty("metadata").GetProperty("finishedOn").GetDateTimeOffset();
        if (run.GetProperty("builder").GetProperty("id").GetString() != trust.BuilderId
            || finished > DateTimeOffset.UtcNow.AddMinutes(5) || finished < DateTimeOffset.UtcNow.AddDays(-trust.MaximumAttestationAgeDays))
            throw new InvalidOperationException("Provenance builder is untrusted or attestation is stale.");
        JsonElement build = predicate.GetProperty("buildDefinition");
        if (string.IsNullOrWhiteSpace(build.GetProperty("buildType").GetString()))
            throw new InvalidOperationException("Provenance build type is required.");
        JsonElement scan = build.GetProperty("externalParameters").GetProperty("securityScan");
        if (scan.GetProperty("status").GetString() != "passed" || scan.GetProperty("high").GetInt32() != 0 || scan.GetProperty("critical").GetInt32() != 0)
            throw new InvalidOperationException("Publisher's signed security scan did not pass policy.");

        using JsonDocument document = JsonDocument.Parse(sbom);
        JsonElement inventory = document.RootElement;
        RequireUniqueProperties(inventory);
        if (inventory.GetProperty("spdxVersion").GetString() != "SPDX-2.3" || inventory.GetProperty("SPDXID").GetString() != "SPDXRef-DOCUMENT"
            || inventory.GetProperty("dataLicense").GetString() != "CC0-1.0"
            || !Uri.TryCreate(inventory.GetProperty("documentNamespace").GetString(), UriKind.Absolute, out _)
            || inventory.GetProperty("creationInfo").GetProperty("creators").GetArrayLength() == 0)
            throw new InvalidOperationException("SPDX document metadata is incomplete.");
        _ = inventory.GetProperty("creationInfo").GetProperty("created").GetDateTimeOffset();
        foreach ((string subject, string digest) in artifacts)
        {
            JsonElement[] matches = inventory.GetProperty("packages").EnumerateArray().Where(package =>
                package.GetProperty("externalRefs").EnumerateArray().Any(reference => reference.GetProperty("referenceType").GetString() == "purl"
                    && reference.GetProperty("referenceLocator").GetString() == subject)).ToArray();
            if (matches.Length != 1 || !matches[0].GetProperty("checksums").EnumerateArray().Any(checksum =>
                    checksum.GetProperty("algorithm").GetString() == "SHA256" && checksum.GetProperty("checksumValue").GetString() == digest))
                throw new InvalidOperationException("SPDX artifact identity/checksum does not match the signed release.");
            string id = matches[0].GetProperty("SPDXID").GetString()!;
            int versionSeparator = subject.LastIndexOf('@');
            string packageName = Uri.UnescapeDataString(subject[(subject.IndexOf('/') + 1)..versionSeparator]);
            string packageVersion = subject[(versionSeparator + 1)..];
            if (matches[0].GetProperty("name").GetString() != packageName || matches[0].GetProperty("versionInfo").GetString() != packageVersion
                || !inventory.GetProperty("relationships").EnumerateArray().Any(relationship =>
                    relationship.GetProperty("spdxElementId").GetString() == "SPDXRef-DOCUMENT"
                    && relationship.GetProperty("relationshipType").GetString() == "DESCRIBES"
                    && relationship.GetProperty("relatedSpdxElement").GetString() == id))
                throw new InvalidOperationException("SPDX must describe each release artifact.");
        }
    }

    private static void RequireUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidOperationException("Duplicate JSON properties are forbidden.");
                RequireUniqueProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement child in value.EnumerateArray()) RequireUniqueProperties(child);
    }

    private static string PackageSubject(ModulePackageIdentity package, bool npm) =>
        $"pkg:{(npm ? "npm" : "nuget")}/{package.Id.Replace("@", "%40", StringComparison.Ordinal)}@{package.Version}";
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static XDocument ReadXml(byte[] bytes)
    {
        using MemoryStream stream = new(bytes, writable: false);
        using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader);
    }
}
