# Flatpack

> Ship flat. Assemble fast.

Flatpack is an Apache-2.0 enterprise application template for .NET 10, PostgreSQL, React, TanStack, Aspire, and OpenTelemetry.

## Install and create

```bash
dotnet new install Flatpack.Templates
dotnet new flatpack -n Horizon
```

React is included by default. Create a backend-only solution with:

```bash
dotnet new flatpack -n Horizon --ui none
```

Optional modules are enabled with `--email`, `--storage`, `--documents`, and `--images`.

### Install from a local package

```bash
dotnet pack -c Release -o artifacts/packages
dotnet new install artifacts/packages/Flatpack.Templates.0.1.0-preview.1.nupkg --force
dotnet new flatpack -n Horizon
```

The installed template uses the standard .NET template engine. Rider can install the `.nupkg` from **New Solution → More Templates → Install Template**, or discover it after `dotnet new install`. Visual Studio discovers installed SDK templates in the **Create a new project** dialog; search for **Flatpack** after installing the package and restarting the dialog or IDE.

The generated solution includes organization RBAC, PostgreSQL RLS, a platform-only Tenant Management console with first-owner invitations, customizable platform roles backed by a published permission catalog, a transactional outbox, ASP.NET Core Identity and OpenIddict, cookie/BFF authentication, build-time OpenAPI, a generated TanStack Query client, a reusable React component package, Aspire orchestration, and a provisioned Grafana/Loki/Tempo/Prometheus stack.

## Develop the template

```bash
dotnet restore templates/flatpack/FlatpackApp.slnx
dotnet build templates/flatpack/FlatpackApp.slnx --no-restore
dotnet test templates/flatpack/FlatpackApp.slnx --no-build
dotnet pack -c Release -o artifacts/packages
```

The implementation is clean-room. No ASP Nano source, assets, credentials, or branding are included.

Architecture decisions and the dependency policy are documented under [docs](docs/).
The remaining work required before a stable release is tracked in the [production-readiness plan](docs/production-readiness-plan.md).

## Branching and releases

`develop` is the integration and default branch. Feature and fix branches merge into `develop` and are deleted after merge. Release-ready changes merge from `develop` into `main`.

Version tags (`v*`) publish `Flatpack.Templates` to NuGet only when the tagged commit is already present on `main`. Configure the repository's `NUGET_API_KEY` Actions secret before creating the first release tag.
