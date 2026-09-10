using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Trykatch.ModuleTool;

public sealed partial class ModuleWorkspace
{
    private string PinnedBackendArchive(ModulePackageIdentity package) => ResolveInsideRoot(Path.Combine(
        ".trykatch", "packages", package.Sha256!.ToLowerInvariant(), $"{package.Id}.{package.Version}.nupkg"));

    private string PinnedWebSpecifier(ModuleManifest manifest)
    {
        string artifact = PinnedWebArchive(manifest);
        return "file:" + Path.GetRelativePath(ResolveInsideRoot("web/apps/web"), artifact).Replace(Path.DirectorySeparatorChar, '/');
    }

    private string PinnedWebArchive(ModuleManifest manifest) => ResolvePackageArtifact(
        PackageManifestPath(manifest), manifest.Distribution.Web!.PackageFile, "frontend");

    private void MaterializeVerifiedPackage(CandidatePackage candidate, string destination)
    {
        string directory = Path.GetDirectoryName(destination)!;
        foreach ((string relative, byte[] bytes) in candidate.VerifiedFiles!)
        {
            string target = ResolveInsideRoot(Path.GetFullPath(relative, directory));
            WriteAtomicBytes(target, bytes);
        }
        ModulePackageIdentity package = candidate.Manifest.Distribution.Dotnet!;
        string reviewedArchive = Path.GetRelativePath(Path.GetDirectoryName(candidate.ManifestPath)!,
            ResolvePackageArtifact(candidate.ManifestPath, package.PackageFile, "backend"));
        string feedArchive = PinnedBackendArchive(package);
        WriteAtomicBytes(feedArchive, candidate.VerifiedFiles![reviewedArchive]);

        string path = ResolveInsideRoot("NuGet.Config");
        XDocument config = ReadXml(File.ReadAllBytes(path));
        XElement root = config.Root ?? throw new InvalidOperationException("Invalid NuGet.Config.");
        XElement sources = Child(root, "packageSources");
        XElement mapping = Child(root, "packageSourceMapping");
        string sourceKey = "trykatch-module-" + candidate.Manifest.Id;
        sources.Elements("add").Where(item => (string?)item.Attribute("key") == sourceKey).Remove();
        sources.Add(new XElement("add", new XAttribute("key", sourceKey),
            new XAttribute("value", Path.GetRelativePath(_root, Path.GetDirectoryName(feedArchive)!))));
        // An exact source mapping outranks wildcard mappings. Remove conflicting
        // exact entries so this ID can only come from its verified local feed.
        mapping.Descendants("package").Where(item => string.Equals((string?)item.Attribute("pattern"), package.Id, StringComparison.OrdinalIgnoreCase)).Remove();
        mapping.Elements("packageSource").Where(item => (string?)item.Attribute("key") == sourceKey).Remove();
        mapping.Add(new XElement("packageSource", new XAttribute("key", sourceKey), new XElement("package", new XAttribute("pattern", package.Id))));
        XElement settings = Child(root, "config");
        settings.Elements("add").Where(item => (string?)item.Attribute("key") == "globalPackagesFolder").Remove();
        settings.Add(new XElement("add", new XAttribute("key", "globalPackagesFolder"),
            new XAttribute("value", Path.GetRelativePath(_root, candidate.RestoreCachePath!))));
        WriteAtomic(path, config.ToString() + "\n");
    }

    private static XElement Child(XElement parent, string name)
    {
        XElement? existing = parent.Element(name);
        if (existing is not null) return existing;
        XElement child = new(name);
        parent.Add(child);
        return child;
    }

    private static void VerifyRestoredBackend(CandidatePackage candidate)
    {
        ModulePackageIdentity package = candidate.Manifest.Distribution.Dotnet!;
        string id = package.Id.ToLowerInvariant();
        string restored = Path.Combine(candidate.RestoreCachePath!, id, package.Version, $"{id}.{package.Version}.nupkg");
        if (!File.Exists(restored)) throw new InvalidOperationException("Restore did not produce the reviewed backend artifact.");
        VerifyDigest(restored, package.Sha256, "restored backend");
    }

    private void VerifyRestoredWeb(CandidatePackage candidate)
    {
        ModulePackageIdentity? package = candidate.Manifest.Distribution.Web;
        if (package is null || !HasWebSurface()) return;

        string archive = PinnedWebArchive(candidate.Manifest);
        VerifyDigest(archive, package.Sha256, "restored frontend");
        byte[] bytes = File.ReadAllBytes(archive);
        VerifyArchiveIdentity(bytes, package, candidate.Manifest.Version, npm: true);

        string packageJson = ResolveInsideRoot("web/apps/web/package.json");
        if (!HasWebDependency(packageJson, package.Id, PinnedWebSpecifier(candidate.Manifest)))
            throw new InvalidOperationException("Restore did not retain the reviewed frontend artifact path.");

        string lockText = File.ReadAllText(ResolveInsideRoot("web/pnpm-lock.yaml"));
        string integrity = "sha512-" + Convert.ToBase64String(SHA512.HashData(bytes));
        if (!lockText.Contains(package.Id, StringComparison.Ordinal)
            || !lockText.Contains(package.Version, StringComparison.Ordinal)
            || !lockText.Contains(integrity, StringComparison.Ordinal))
            throw new InvalidOperationException("Restore did not bind the reviewed frontend identity, version and integrity.");
    }

    private PackageMutationLock AcquirePackageMutationLock()
    {
        string name = PackageMutationLockName(_root);
        Mutex mutex = new(false, name);
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(30))) throw new InvalidOperationException("Another module transaction is in progress.");
            return new PackageMutationLock(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }

    internal static string PackageMutationLockName(string root)
    {
        string identity = ResolvePhysicalDirectoryPath(root);
        return "trykatch-module-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private sealed class PackageMutationLock(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
}
