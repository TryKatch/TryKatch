using System.Text.Json;

namespace Trykatch.ModuleTool;

public sealed partial class ModuleWorkspace
{
    private static readonly string[] FactGuides = ["AGENTS.md", "docs/development/backend.md", "docs/development/frontend.md", "docs/development/security.md", "docs/development/verification.md"];
    /// <summary>Read-only facts from validated installed manifests, not a second hand-maintained module catalog.</summary>
    public JsonElement Facts(string? moduleId = null)
    {
        ModuleDoctorReport inspection = Inspect();
        if (!inspection.IsHealthy) throw new InvalidOperationException(string.Join(Environment.NewLine, inspection.Errors));
        List<string> errors = [];
        ModuleCatalogFile catalog = ReadJson<ModuleCatalogFile>(_catalogPath, errors, "module catalog")
            ?? throw new InvalidOperationException("Module catalog is unavailable.");
        List<LoadedModule> loaded = LoadModules(catalog, errors);
        ValidateModules(catalog, loaded, errors);
        if (errors.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        if (moduleId is not null && loaded.All(module => module.Manifest.Id != moduleId))
            throw new ArgumentException($"Installed module '{moduleId}' was not found.");
        return JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 1,
            hostVersion = catalog.HostVersion,
            modules = loaded.Where(module => moduleId is null || module.Manifest.Id == moduleId)
                .OrderBy(module => module.Manifest.Id, StringComparer.Ordinal).Select(module => new
                {
                    module.Manifest.Id, module.Manifest.Name, module.Manifest.Version,
                    module.Registration.Enabled,
                    source = new { manifest = module.Registration.Manifest, module.Manifest.Artifacts, module.Manifest.Entrypoints },
                    module.Manifest.Distribution.Kind,
                    module.Manifest.Requires, module.Manifest.OptionalDependencies,
                    module.Manifest.Capabilities, module.Manifest.DataOwnership,
                    module.Manifest.Contributions,
                    assistantExecution = "Descriptors are metadata; runtime requires an explicitly registered read adapter. Writes are disabled in v1."
                }),
            guides = FactGuides
        }, JsonOptions);
    }
}
