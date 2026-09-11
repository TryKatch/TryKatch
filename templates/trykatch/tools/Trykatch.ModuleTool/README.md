# Trykatch CLI

The Trykatch CLI validates and composes the backend and React surfaces of a generated Trykatch application from one authoritative `trykatch.modules.json` catalog.

```bash
dotnet tool install --global Trykatch.Cli --prerelease
trykatch help
trykatch template install
trykatch new Horizon
trykatch template help
trykatch start
trykatch module help
trykatch module doctor --root /path/to/application
trykatch module register src/My.Module/module-manifest.json
trykatch module install ./downloaded/module-manifest.json --sha256 <published-digest>
trykatch module upgrade ./downloaded/module-manifest.json --sha256 <published-digest>
trykatch module disable <id>
trykatch module unregister <id>
trykatch module eject <id> --source-bundle ./reviewed-source --sha256 <published-digest>
trykatch template uninstall
```

`template install` invokes the official .NET template engine with an argument-safe process boundary. It shows a spinner in interactive terminals, emits deterministic progress in redirected output and CI, preserves the template engine's failure details, and defaults to the template version matching the installed CLI. Use `--version <version>` to select another release and `--force` to repair an existing installation.

`new <name>` creates the complete application and authorizes the packaged Git
initialization post-action. Standalone output starts on `main`; output already
inside a Git worktree remains part of its parent repository.

`start` discovers the generated Aspire AppHost from the current directory or `--root`, then runs its HTTPS launch profile with inherited terminal output. `template uninstall` removes `Trykatch.Templates` through the official .NET template engine; remove the global CLI separately with `dotnet tool uninstall --global Trykatch.Cli`.

`trykatch.modules.lock.json` is machine-owned and records the manifest digest mode,
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
