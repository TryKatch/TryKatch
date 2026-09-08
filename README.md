# Flatpack

> Ship flat. Assemble fast.

Flatpack is an Apache-2.0 enterprise application template for .NET 10, PostgreSQL, React, TanStack, Aspire, and OpenTelemetry.

## Install and create

`Flatpack.Templates` is the full-stack project generator. Install it once through the standard .NET template engine, then generate as many independent applications as needed. Every default generation includes the .NET solution and the complete React/TanStack frontend workspace:

```bash
dotnet new install Flatpack.Templates
dotnet new flatpack -n Horizon
dotnet new flatpack -n Acme.Operations
```

The default output includes `web/apps/web`, the reusable `web/packages/ui` component system, the generated TanStack Query API client, frontend tests, and the production web container. Create a backend-only solution only when it is explicitly requested with:

```bash
dotnet new flatpack -n Horizon --ui none
```

Optional modules are enabled with `--email`, `--storage`, `--documents`, and `--images`.

`Flatpack.Cli` is the optional module lifecycle tool. Every generated application also contains its source under `tools/<ApplicationName>.ModuleTool`, so developers can run it without a global installation. Installing the packaged command provides the shorter `flatpack` command:

```bash
dotnet tool install --global Flatpack.Cli
flatpack module doctor --root ./Horizon
```

The two packages have separate responsibilities:

- `Flatpack.Templates` creates a complete renamed .NET and React solution through `dotnet new`, Rider, or Visual Studio.
- `Flatpack.Cli` validates and composes the generated application's enabled backend and React modules.

### Install from a local package

```bash
dotnet pack Flatpack.Templates.csproj -c Release -o artifacts/packages
dotnet pack templates/flatpack/tools/FlatpackApp.ModuleTool -c Release -o artifacts/packages
dotnet new install artifacts/packages/Flatpack.Templates.0.1.0-preview.1.nupkg --force
dotnet new flatpack -n Horizon
dotnet tool install --global Flatpack.Cli --version 0.1.0-preview.1 --add-source artifacts/packages
```

The installed template uses the standard .NET template engine. Rider can install the `.nupkg` from **New Solution → More Templates → Install Template**, or discover it after `dotnet new install`. Visual Studio discovers installed SDK templates in the **Create a new project** dialog; search for **Flatpack** after installing the package and restarting the dialog or IDE.

The generated solution includes organization RBAC, PostgreSQL RLS, a platform-only Tenant Management console with first-owner invitations, customizable platform roles backed by a published permission catalog, a transactional outbox, ASP.NET Core Identity and OpenIddict, cookie/BFF authentication, build-time OpenAPI, development-only Scalar API documentation, a generated TanStack Query client, a deny-by-default assistant tool contract, a reusable React component package, Aspire orchestration, and a provisioned Grafana/Loki/Tempo/Prometheus stack.

## Develop the template

```bash
dotnet restore templates/flatpack/FlatpackApp.slnx
dotnet build templates/flatpack/FlatpackApp.slnx --no-restore
dotnet test templates/flatpack/FlatpackApp.slnx --no-build
dotnet pack -c Release -o artifacts/packages
```

The implementation is clean-room. No ASP Nano source, assets, credentials, or branding are included.

Architecture decisions and the dependency policy are documented under [docs](docs/).
The module seam and implementation sequence are documented in the [module-authoring direction](docs/module-authoring.md), with primary-source research in [the OpenMercato modularity note](docs/research/openmercato-modularity-research.md).
The remaining work required before a stable release is tracked in the [production-readiness plan](docs/production-readiness-plan.md). Security assumptions and target-environment responsibilities are explicit in the [threat model](docs/threat-model.md).

## Branching and releases

`develop` is the integration and default branch. Feature and fix branches merge into `develop` and are deleted after merge. Release-ready changes merge from `develop` into `main`.

Version tags (`v*`) publish `Flatpack.Templates` and `Flatpack.Cli` to NuGet only when the tagged commit is already present on `main`. Configure the repository's `NUGET_API_KEY` Actions secret before creating the first release tag.
