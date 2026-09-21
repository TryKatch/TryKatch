# Trykatch

> Start secure. Build freely.

Trykatch is an Apache-2.0 enterprise application template for .NET 10, PostgreSQL, React, TanStack, Aspire, and OpenTelemetry.

## Install and create

`Trykatch.Templates` is the full-stack project generator. The recommended installer uses the official .NET template engine underneath while adding clear progress and completion states. Every default generation includes the .NET solution and the complete React/TanStack frontend workspace:

```bash
dotnet tool install --global Trykatch.Cli --version 0.1.0-preview.32
trykatch template install
trykatch new Horizon
trykatch new Acme.Operations
cd Horizon
trykatch start
```

The recommended `trykatch new` command initializes each standalone application
as a Git repository with `main` as its initial branch. If the output directory
is already inside a Git worktree, Trykatch preserves the parent repository
instead of creating a nested one.

For automation or IDE-managed environments, the direct Microsoft CLI command remains supported:

```bash
dotnet new install Trykatch.Templates@0.1.0-preview.32
```

The default output includes `web/apps/web`, the reusable `web/packages/ui` component system, the generated TanStack Query API client, frontend tests, and the production web container. Create a backend-only solution only when it is explicitly requested with:

```bash
trykatch new Horizon --ui none
```

Optional modules are enabled with `--email`, `--storage`, `--documents`, and `--images`.

Set a separate customer-facing brand with `trykatch new Kamenta.App --display-name "Kamenta" --email true`. The technical namespace stays `Kamenta.App`; UI and email branding use `Kamenta`.

`Trykatch.Cli` installs, updates, or removes the project template, starts generated applications through their Aspire AppHost, and manages the module lifecycle. Every generated application also contains its source under `tools/<ApplicationName>.ModuleTool`, so developers can run module commands without a global installation:

```bash
dotnet tool install --global Trykatch.Cli --version 0.1.0-preview.32
trykatch update
trykatch help
trykatch template help
trykatch start --help
trykatch module help
trykatch module doctor --root ./Horizon
```

`trykatch update` is idempotent: when the requested template version is already installed, it reports success without removing or reinstalling it. Pass `--force` only to deliberately repair that same version by reinstalling it.

From the generated application root, create a secure organization-owned backend
module—or add `--with-web` for its React surface—in one atomic command:

```bash
cd Horizon
trykatch module create Invoicing \
  --entity Invoice \
  --resource invoices \
  --ownership organization \
  --with-web
```

The generator registers and enables the module, updates the solution and package
graphs, builds and tests it, and restores the original workspace if any phase
fails. Live step-by-step progress and elapsed time show what is running; redirected
output uses plain log lines. See the [module authoring guide](https://docs.trykatch.net/modules/authoring/)
for the generated structure, endpoints, RLS contract, and extension rules.

Project creation (`trykatch new`), module creation, registry generation and module
package operations show loading feedback. Template installation and updates also
show progress. Use the Trykatch CLI for this experience; direct `dotnet` commands
keep the .NET SDK's own output.

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
dotnet pack templates/trykatch/tools/Trykatch.ModuleTool -c Release -o artifacts/packages
dotnet new install artifacts/packages/Trykatch.Templates.0.1.0-preview.32.nupkg --force
dotnet new trykatch -n Horizon --allow-scripts yes
dotnet tool install --global Trykatch.Cli --version 0.1.0-preview.32 --add-source artifacts/packages
```

The installed template uses the standard .NET template engine. Rider can install the `.nupkg` from **New Solution → More Templates → Install Template**, or discover it after `dotnet new install`. Visual Studio discovers installed SDK templates in the **Create a new project** dialog; search for **Trykatch** after installing the package and restarting the dialog or IDE.

The generated solution includes organization RBAC, PostgreSQL RLS, a platform-only Tenant Management console with first-owner invitations, customizable platform roles backed by a published permission catalog, a transactional outbox, ASP.NET Core Identity and OpenIddict, cookie/BFF authentication, build-time OpenAPI, development-only Scalar API documentation, a generated TanStack Query client, a deny-by-default assistant tool contract, a reusable React component package, Aspire orchestration, and a provisioned Grafana/Loki/Tempo/Prometheus stack.

## AI-assisted development

Generated applications include an architectural task router and five project-local skills: `trykatch-spec`, `trykatch-build-module`, `trykatch-extend-module`, `trykatch-review`, and `trykatch-verify`. They use the application's module generator, business blueprints, reference code, and verification commands to take a feature brief through implementation and review. They also work with backend-only output and preserve application namespace replacement.

Ask your coding agent to read `.agents/skills/trykatch-build-module/SKILL.md` in the generated application and implement your feature. Automatic discovery depends on the agent; direct file invocation works without installing global skills. See the [bundled workflow guide](templates/trykatch/docs/ai-assisted-development.md). The skills support development in your existing coding agent. User-facing AI Help is also included in generated applications, with a floating chat button and account-menu entry. Its provider-neutral, read-only runtime is opt-in; configure its API server before making model calls. State-changing AI tools and an approval executor are not included.

New applications also receive [developer onboarding](templates/trykatch/docs/developer-onboarding.md) ([Français](templates/trykatch/docs/developer-onboarding.fr.md)) and a generation-time **Start here** message. Use the read-only orientation prompt and question list in your existing coding assistant to learn module creation, backend/frontend wiring, integration contracts and verification, without another onboarding AI key. Backend-only output skips frontend steps. In-app AI Help is unchanged.

Template maintainers can run `bash scripts/test-development-skills.sh` to verify the packed bundle in renamed React and backend-only applications. The [validation guide](docs/development-skills-validation.md) also describes independent behavioral trials.

## Develop the template

```bash
dotnet restore templates/trykatch/Trykatch.slnx
dotnet build templates/trykatch/Trykatch.slnx --no-restore
dotnet test templates/trykatch/Trykatch.slnx --no-build
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
