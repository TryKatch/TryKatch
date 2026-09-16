---
name: trykatch-build-module
description: Build a new Trykatch business module from a brief or specification using the installed module generator, business blueprints, and focused custom logic. Use for new modules; use trykatch-extend-module for existing modules.
---

# Build a business module

Read [AGENTS.md](../../../AGENTS.md), the [backend guide](../../../docs/development/backend.md), and the applicable spec. For a substantial brief without a spec, use [trykatch-spec](../trykatch-spec/SKILL.md) first and continue implementation when decisions are sufficient.

Inspect `trykatch.modules.json`, `src/Modules/`, the working tree, and whether `web/package.json` exists. If the capability already has an owner, use [trykatch-extend-module](../trykatch-extend-module/SKILL.md). Resolve collisions before creating files. Use the application-local CLI through `dotnet run --project tools/Trykatch.ModuleTool --` so the tool matches the installed source; use an installed CLI only after verifying compatibility.

Choose the smallest supported starting point:

- Plain organization CRUD: `module create` with `--fields`.
- Explicit decisions and transitions: author a JSON blueprint, run `module validate --blueprint <path>`, then `module create <Module> --blueprint <path>`.
- Platform/global ownership or unsupported domain shape: follow the manual authoring section of [modules.md](../../../docs/modules.md). Keep the same module interfaces and project boundaries.

The [CLI reference](../../../tools/Trykatch.ModuleTool/README.md) owns syntax and blueprint limits. Do not combine `--blueprint` with `--fields`, `--entity`, `--resource`, `--ownership`, or `--description`. Do not add a host capability marker merely to bypass compatibility checks. Do not run a creation example unchanged unless its domain matches the request.

Add `--with-web` only when the user needs a UI and the application includes the web workspace; read the [frontend guide](../../../docs/development/frontend.md). On backend-only output, explain that adding a frontend would expand the application before attempting it.

Let creation finish its built-in verification. If it fails, inspect the retained diagnostics and confirm rollback before retrying; do not bypass failed validation. After generation, implement the remaining business rules in domain/application code, add forward-only migrations as needed, and add tests for the requested behavior. A generated CRUD slice is not completion of a richer workflow.

Use the [security guide](../../../docs/development/security.md) for permissions and persistence. Regenerate owned artifacts after custom changes, then follow [trykatch-verify](../trykatch-verify/SKILL.md). Credit checks already run successfully by the CLI rather than repeating them without a new change.

Before handing off, apply [trykatch-review](../trykatch-review/SKILL.md) to the task diff and specification. Resolve substantive findings within the implementation request and rerun checks affected by those fixes. This can be a local review pass; it does not require another agent or an external PR.

Update the spec with implemented acceptance cases and remaining gaps. Report the module's entry points, verified behavior, and any checks that could not run. Do not label untested behavior complete.
