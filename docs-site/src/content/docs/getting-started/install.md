---
title: Install and create an application
description: Install the Trykatch template package and generate a complete .NET and React solution.
---

## Install the template

```bash
dotnet new install Trykatch.Templates@0.1.0-preview.3
```

Generate a complete .NET 10 backend and React workspace with your product name:

```bash
dotnet new trykatch -n Horizon
cd Horizon
```

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

## IDE installation

The package uses the standard .NET template engine. Rider can install the `.nupkg` from **New Solution → More Templates → Install Template**. Visual Studio discovers installed SDK templates in **Create a new project** after the package is installed; search for **Trykatch**.

:::caution[Pre-release status]
Trykatch is a production-oriented preview, not a stable production certification. Every generated product must complete its own security, recovery, capacity, and operational qualification.
:::
