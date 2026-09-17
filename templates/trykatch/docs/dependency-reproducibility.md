# Dependency reproducibility

Run commands from the generated application root. `global.json` requires the tested .NET 10.0.3xx SDK feature band, starting at 10.0.301, and permits patch roll-forward only. Install that band explicitly even if another .NET SDK is already installed. Upgrade the policy and generated workflow SDK selection together after qualification.

The generated workflows pin third-party actions by commit. React output also pins pnpm through its root and web manifests; use Corepack. Backend-only output intentionally has no frontend manifest or commands.

NuGet lockfiles from the template source are excluded because their project names and platform-specific AppHost assets are not a generated application's lock graph. Initialize them with:

```bash
dotnet restore Trykatch.slnx
dotnet build Trykatch.slnx --no-restore
```

Review and commit the generated `packages.lock.json` files with the source. First generated CI must not require a cache keyed by nonexistent locks; NuGet caching is therefore not enabled in the shipped workflows. React CI can use its shipped `web/pnpm-lock.yaml` immediately.

For a committed runtime project graph, a locked restore rejects an unreviewed dependency change:

```bash
dotnet restore src/API/Trykatch.Api/Trykatch.Api.csproj --locked-mode
```

Exercise that command on the supported runner before enforcing it. Aspire AppHost includes an SDK/runtime/platform-specific dashboard dependency; its lockfile needs separate platform qualification. The shipped solution-wide restore is not claimed to enforce locked mode after initial generation. Completing that transition, clean Windows/macOS/Linux first CI and manual IDE qualification remains a template readiness gate. Do not silently disable runtime-project locked checks to accommodate an AppHost mismatch.

After a reviewed package/module change, regenerate affected locks with a normal restore, inspect their differences, rebuild and rerun the relevant PostgreSQL/contract tests before committing. Existing generated products do not receive these source/workflow changes automatically when the template package is updated.
