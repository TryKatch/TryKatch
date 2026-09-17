---
name: trykatch-extend-module
description: Extend or fix an existing Trykatch module while preserving application customizations, public contracts, migrations, and module boundaries. Use for changes to existing business behavior, APIs, or module UI.
---

# Extend an existing module

Read [AGENTS.md](../../../AGENTS.md), the owning module's manifest and code, and the applicable spec. Consult the [backend guide](../../../docs/development/backend.md) and, for UI changes, the [frontend guide](../../../docs/development/frontend.md).

Trace the behavior from the endpoint or page to its use case, domain method, persistence adapter, and tests. Establish whether the application owns the source or consumes a package. Distinguish generated registries/clients from scaffolded module source: scaffolded domain and UI files are intended for customization; registries and clients are regenerated.

Select the change surface:

- Change application-owned behavior in its existing module. Do not rerun `module create` over it or regenerate its customized source from a retained blueprint.
- Contribute to another module through a named extension point or its IntegrationEvents contract. Do not import private implementation projects/components.
- For an installed package without a suitable extension, describe the missing contract or use the documented reviewed-source ejection workflow if source ownership is within the request. Do not patch package caches or silently change distribution ownership.

For a substantial new behavior, use [trykatch-spec](../trykatch-spec/SKILL.md) and continue with implementation when the user's intent supplies the necessary decisions. For a bug, reproduce the failure or identify the violated invariant before changing it; add a focused regression check when useful.

Preserve existing operation IDs, event names/payload compatibility, permissions, extension IDs, and numeric transports unless the requested change explicitly requires a migration. Find callers before changing a public contract. Add a forward migration instead of editing an applied one. For blueprint-created modules, update the retained `module.blueprint.json` to describe supported contract changes, but implement source changes manually; record custom behavior outside the blueprint's expressiveness in the spec.

Consult the [security guide](../../../docs/development/security.md) for access/data changes. Regenerate registries only when module composition changes; rebuild OpenAPI and the client when API contracts change. Use [trykatch-verify](../trykatch-verify/SKILL.md), inspect the final diff for unrelated generated drift, and update the affected specification or user documentation.

Apply [trykatch-review](../trykatch-review/SKILL.md) to the resulting task diff before handoff. Address substantive findings within the requested implementation and rerun only checks affected by further changes. A local review pass is sufficient; do not create an external PR unless it is part of the user's workflow.
