---
title: Author a module
description: Add a capability without weakening the security kernel or creating hidden runtime coupling.
---

A Trykatch module is a full-stack capability package with an explicit contract. Projects demonstrates a built-in capability, Federation demonstrates an optional platform adapter, and Documents proves an independently packaged organization-data module across .NET, React, migrations, permissions, audit, and lifecycle behavior.

## What a module can contribute

- API services and controllers;
- EF Core model configuration and migrations;
- stable permissions and safe role defaults;
- React routes, navigation, and named UI extensions;
- outbox events, subscribers, and workers;
- explicitly allowlisted assistant tools.

## What remains centralized

Identity, organization resolution, RLS, antiforgery, permission enforcement, audit integrity, and module validation are not extension points.

## Development flow

1. Declare stable module and contribution identifiers.
2. Register backend and React contributions in the paired module catalog.
3. Implement domain, application, infrastructure, API, and UI behavior inside the module boundary.
4. Add permissions, organization defaults, migrations, RLS policies, audit events, and outbox behavior.
5. Run the module doctor, dependency checks, disablement tests, and the generated-template matrix.

Persistent modules must also satisfy the [module data-isolation contract](/architecture/module-data-isolation/). Modules cannot opt out of organization scoping or receive direct access to host database contexts.

```bash
trykatch module list --root ./Horizon
trykatch module doctor --root ./Horizon
```

:::caution
Third-party package acquisition, upgrade, eject, unregister, and purge workflows remain release gates. The current seam is deliberately build-time and validated; it is not arbitrary runtime plugin loading.
:::
