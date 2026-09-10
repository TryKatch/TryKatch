# Trykatch

> Start secure. Build freely.

Trykatch is an Apache-2.0 enterprise application template for .NET 10, PostgreSQL, React, TanStack, Aspire, and OpenTelemetry.

## Install and create

`Trykatch.Templates` is the full-stack project generator. The recommended installer uses the official .NET template engine underneath while adding clear progress and completion states. Every default generation includes the .NET solution and the complete React/TanStack frontend workspace:

```bash
dotnet tool install --global Trykatch.Cli --version 0.1.0-preview.8
trykatch template install
dotnet new trykatch -n Horizon
dotnet new trykatch -n Acme.Operations
cd Horizon
trykatch start
```

For automation or IDE-managed environments, the direct Microsoft CLI command remains supported:

```bash
dotnet new install Trykatch.Templates@0.1.0-preview.8
```

The default output includes `web/apps/web`, the reusable `web/packages/ui` component system, the generated TanStack Query API client, frontend tests, and the production web container. Create a backend-only solution only when it is explicitly requested with:

```bash
dotnet new trykatch -n Horizon --ui none
```

Optional modules are enabled with `--email`, `--storage`, `--documents`, and `--images`.

`Trykatch.Cli` installs, updates, or removes the project template, starts generated applications through their Aspire AppHost, and manages the module lifecycle. Every generated application also contains its source under `tools/<ApplicationName>.ModuleTool`, so developers can run module commands without a global installation:

```bash
dotnet tool install --global Trykatch.Cli --version 0.1.0-preview.8
trykatch update
trykatch help
trykatch template help
trykatch start --help
trykatch module help
trykatch module doctor --root ./Horizon
```

The two packages have separate responsibilities:

- `Trykatch.Templates` creates a complete renamed .NET and React solution through `dotnet new`, Rider, or Visual Studio.
- `Trykatch.Cli` manages the template with progress feedback, starts its AppHost, then validates and composes the generated application's enabled backend and React modules.

To remove Trykatch tooling, uninstall the template before removing the CLI:

```bash
trykatch template uninstall
dotnet tool uninstall --global Trykatch.Cli
```

### Install from a local package

```bash
dotnet pack Trykatch.Templates.csproj -c Release -o artifacts/packages
dotnet pack templates/trykatch/tools/TrykatchApp.ModuleTool -c Release -o artifacts/packages
dotnet new install artifacts/packages/Trykatch.Templates.0.1.0-preview.8.nupkg --force
dotnet new trykatch -n Horizon
dotnet tool install --global Trykatch.Cli --version 0.1.0-preview.8 --add-source artifacts/packages
```

The installed template uses the standard .NET template engine. Rider can install the `.nupkg` from **New Solution → More Templates → Install Template**, or discover it after `dotnet new install`. Visual Studio discovers installed SDK templates in the **Create a new project** dialog; search for **Trykatch** after installing the package and restarting the dialog or IDE.

The generated solution includes organization RBAC, PostgreSQL RLS, a platform-only Tenant Management console with first-owner invitations, customizable platform roles backed by a published permission catalog, a transactional outbox, ASP.NET Core Identity and OpenIddict, cookie/BFF authentication, build-time OpenAPI, development-only Scalar API documentation, a generated TanStack Query client, a deny-by-default assistant tool contract, a reusable React component package, Aspire orchestration, and a provisioned Grafana/Loki/Tempo/Prometheus stack.

## Develop the template

```bash
dotnet restore templates/trykatch/TrykatchApp.slnx
dotnet build templates/trykatch/TrykatchApp.slnx --no-restore
dotnet test templates/trykatch/TrykatchApp.slnx --no-build
dotnet pack -c Release -o artifacts/packages
```

The implementation is independently designed and contains no third-party proprietary source, assets, credentials, or branding.

Architecture decisions and the dependency policy are documented under [docs](docs/).
The public, searchable Markdown documentation is built with Astro Starlight from [docs-site](docs-site/) and published at [docs.trykatch.net](https://docs.trykatch.net).
The module seam and implementation sequence are documented in the [module-authoring direction](docs/module-authoring.md), with primary-source research in [the OpenMercato modularity note](docs/research/openmercato-modularity-research.md).
The remaining work required before a stable release is tracked in the [production-readiness plan](docs/production-readiness-plan.md). Security assumptions and target-environment responsibilities are explicit in the [threat model](docs/threat-model.md).

## Branching and releases

`develop` is the integration and default branch. Feature and fix branches merge into `develop` and are deleted after merge. Release-ready changes merge from `develop` into `main`.

Version tags (`v*`) publish `Trykatch.Templates` and `Trykatch.Cli` to NuGet through the repository's trusted-publishing workflow, only when the tagged commit is already present on `main`.
