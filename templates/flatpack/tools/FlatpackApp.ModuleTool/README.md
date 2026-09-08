# Flatpack CLI

The Flatpack CLI validates and composes the backend and React surfaces of a generated Flatpack application from one authoritative `flatpack.modules.json` catalog.

```bash
dotnet tool install --global Flatpack.Cli --prerelease
flatpack module doctor --root /path/to/application
flatpack module register src/My.Module/flatpack.module.json
flatpack module install ./downloaded/flatpack.module.json --sha256 <published-digest>
flatpack module upgrade ./downloaded/flatpack.module.json --sha256 <published-digest>
flatpack module disable <id>
flatpack module unregister <id>
flatpack module eject <id> --source-bundle ./reviewed-source --sha256 <published-digest>
```

`flatpack.modules.lock.json` is machine-owned and records the manifest digest mode,
digest, version, enablement state, distribution kind, package pairing, and license
for every module. Installed packages use their exact SHA-256. Workspace manifests
use a template-name-normalized SHA-256 so a newly generated application keeps valid
provenance after `dotnet new -n` replaces its root namespace. Package install and
upgrade require an exact SHA-256 obtained through a separate trusted channel. They
pin the NuGet and npm identities declared by the manifest,
restore both dependency graphs with npm lifecycle scripts disabled, and roll the
catalog, registries, manifests, project files, and lockfiles back if validation or
restore fails.

New packages install disabled. Enablement is a separate reviewed action. Unregister
requires prior disablement, refuses installed dependents, and retains database data
and migration history. Ejection never overwrites source: it accepts only a matching,
checksum-verified workspace source bundle and switches both hosts to local project
and workspace references.

Use `module list`, `module generate`, `module enable <id>`, and `module disable <id>` to inspect and change the installed module graph. Enable and disable operations use atomic file replacement with rollback on failure and never remove module data. Destructive `purge-data` is intentionally not provided; modules must publish a separate reviewed retention runbook for permanent removal.
