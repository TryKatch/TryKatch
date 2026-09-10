---
title: Install and create an application
description: Install the Trykatch template package and generate a complete .NET and React solution.
---

## Install the template

Follow these steps in order.

### 1. Install the Trykatch CLI

Run this once on your computer:

```bash
dotnet tool install --global Trykatch.Cli --version 0.1.0-preview.6
```

This installs the `trykatch` command. It does not install the project template yet.

### 2. Install the project template

After Step 1 succeeds, run:

```bash
trykatch template install
```

This installs the matching .NET and React template. The command uses Microsoft's official .NET template engine, displays live progress in an interactive terminal, and emits stable log lines in CI.

### 3. Create your application

Replace `Horizon` with your product name:

```bash
dotnet new trykatch -n Horizon
cd Horizon
```

:::note[Alternative: install without the Trykatch CLI]
If you do not want the progress-aware Trykatch CLI, use the following command **instead of Steps 1 and 2**:

```bash
dotnet new install Trykatch.Templates@0.1.0-preview.6
```
:::

React is the default surface. The generated project contains the complete `web` workspace, including the React application, reusable UI and module packages, generated API client, tests, lockfile, and production container.

Names containing dots and hyphens are normalized for C# namespaces, directories, container names, and npm packages.

## Choose the generated surface

React is included by default. Generate a backend-only solution only when that is intentional:

```bash
dotnet new trykatch -n Horizon --ui none
```

Optional free modules are disabled by default:

```bash
dotnet new trykatch -n Horizon \
  --email true \
  --storage true \
  --documents true \
  --images true
```

## Explore available commands

Use the CLI's focused help commands:

```bash
trykatch help
trykatch template help
trykatch module help
```

`trykatch help` lists application-generation choices. `trykatch template help` documents template installation and updates. `trykatch module help` lists every supported module lifecycle command.

## Start in Rider

Open the generated `.slnx` file, allow Rider to restore the solution, and select **`<ApplicationName>.AppHost: https`** as the run configuration. The AppHost profile starts PostgreSQL, runs the migrator, injects the separate runtime database credentials, and then starts the API and React application. Keep Docker running and do not use the API project as the standalone startup project.

From Rider's terminal, the equivalent command is:

```bash
dotnet run --launch-profile https --project src/Horizon.AppHost/Horizon.AppHost.csproj
```

## IDE template installation

The package uses the standard .NET template engine. Rider can install the `.nupkg` from **New Solution → More Templates → Install Template**. Visual Studio discovers installed SDK templates in **Create a new project** after the package is installed; search for **Trykatch**.

:::caution[Pre-release status]
Trykatch is a production-oriented preview, not a stable production certification. Every generated product must complete its own security, recovery, capacity, and operational qualification.
:::
