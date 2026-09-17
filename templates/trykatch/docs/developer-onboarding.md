# Developer onboarding

[Français](developer-onboarding.fr.md)

Want a concrete path first? Follow [from first run to first feature](first-feature.md): startup checks, Equipment creation, reading the output and verification checkpoints.

Start here after generating the application. This guide helps you understand the template, create a module, connect its backend and frontend, integrate capabilities, and verify your changes. It also works without an AI tool.

Use your existing coding assistant with this checkout and its normal configuration. No additional AI provider key, subscription, plugin, or in-app chat is required for this onboarding kit. Your assistant's own usage and data policies still apply: do not share secrets, private records, or sensitive source without authorization. Automatic discovery of project instructions varies by tool; explicitly asking it to read the files works with assistants that support repository/file access. For a chat-only tool, supply reviewed non-sensitive excerpts yourself; it cannot inspect this checkout automatically.

## 1. Ask your coding assistant to orient you

Open the generated application root, containing `Trykatch.slnx` and `trykatch.modules.json`. Paste this starter prompt into your existing coding assistant:

```text
Read AGENTS.md and docs/developer-onboarding.md. Inspect this application's
actual source, module catalog and project-local skills before answering.
Identify its namespace, enabled modules, ownership and whether web/package.json
exists. Use installed module facts if the local tool is available; report missing
prerequisites rather than installing or repairing them without asking.
Explain the architecture, trace one existing request, and show the supported
steps to add a module and connect its frontend when one is installed.
Distinguish inspected implementation from documentation and proposed examples.
Do not modify files, run migrations, start services, install dependencies,
read secret stores, or call an AI provider. End with suggested questions and
the relevant verification commands. Ask which feature I want to build next.
```

This is an orientation request, not permission to implement a feature. When ready, make an explicit implementation request with your business rules. The five bundled [project-local skills](../AGENTS.md) cover specification, creation, extension, review and verification; no global skill installation is needed.

## 2. Get running and inspect what is installed

Follow the generated [README](../README.md) for the prerequisites and local startup. Run from the application root, not `web/`. Start through Aspire AppHost rather than running the API alone: AppHost coordinates local database roles, migrations, API and any installed frontend. Use its resource URLs rather than assuming a port. Deployment uses separate [database](database.md) and [production identity](production-identity.md) instructions; development demo accounts are not production credentials.

Use the source-local CLI so the commands match this application's installed contracts:

```bash
dotnet run --project tools/Trykatch.ModuleTool -- module list
dotnet run --project tools/Trykatch.ModuleTool -- module facts
dotnet run --project tools/Trykatch.ModuleTool -- module doctor
dotnet run --project tools/Trykatch.ModuleTool -- module create --help
```

`module facts` reports validated installed declarations, versions, source/artifact entrypoints, permissions and extension IDs. Declarations are not permission grants or proof a runtime adapter exists. Do not assume every example module is enabled, or that another application's CLI version supports the same options.

## 3. Understand the request and module boundaries

Read [module authoring](modules.md), [backend work](development/backend.md) and [authorization/isolation](development/security.md). Each business module has five backend projects:

- Domain: entities, invariants and business rules.
- Application: use cases, authorization and orchestration.
- Presentation: secured HTTP contracts and endpoint binding.
- Infrastructure: persistence, adapters and explicit module registration.
- IntegrationEvents: the public contracts allowed between modules.

The host references a module's Infrastructure entrypoint; Presentation never references Infrastructure. Modules do not import another module's private implementation. Organization and actor context comes from authenticated membership, not editable request fields. Trace the real middleware/use case/transaction path: permissions, EF ownership and forced PostgreSQL RLS work together. Use real PostgreSQL tests to verify isolation; an in-memory test cannot establish it.

## 4. Create your first capability

For editable organization-owned records, a starter example is:

```bash
dotnet run --project tools/Trykatch.ModuleTool -- module create Equipment \
  --entity EquipmentItem --resource equipment_items --ownership organization \
  --fields "name:string:required:max(120),dailyRate:decimal:required"
```

Choose names/resources that do not already exist. Add `--with-web` only when `web/package.json` exists and you need the generated React list/forms/navigation. Backend-only applications skip the frontend steps; a module-local `Web` directory alone is not an installed frontend host.

Creation stages, registers, builds and tests the supported scaffold, rolling back workspace changes on failure. Do not mutate the same workspace concurrently. Module source is yours to customize; generated registries and clients are not. Do not rerun creation over a customized module.

For guarded state transitions and review decisions, inspect [the shipped blueprint](../blueprints/shipment-reception.json) and [CLI reference](../tools/Trykatch.ModuleTool/README.md), validate a suitable blueprint, then generate. CRUD fields do not enforce workflows. Relationships, cross-record/cross-module rules, platform ownership and unsupported workflow behavior need custom implementation and tests, not invented generator flags. A scaffold is not a completed domain product.

## 5. Connect backend and frontend

The application-owned `trykatch.modules.json` and versioned module manifests declare composition. Run `module generate` when those declarations change; the tool owns the API/migrator registries and, when installed, the web registry. Keep stable module IDs, operation IDs, permission keys and extension IDs.

For a React application, follow [frontend work](development/frontend.md): the module's declared web entrypoint supplies routes/navigation; `web/apps/web/src/module-overrides.ts` is the application-owned customization seam. Use the generated TanStack client and existing cookie/antiforgery helpers, never browser access/refresh tokens. Rebuild the API before generating its OpenAPI client:

```bash
dotnet build Trykatch.slnx
corepack pnpm --dir web install --frozen-lockfile
corepack pnpm --dir web generate
corepack pnpm --dir web generate:check
corepack pnpm --dir web typecheck
corepack pnpm --dir web test
corepack pnpm --dir web build
```

Skip the pnpm commands without a web host. Never hand-edit generated clients or registries to fix drift. Preserve decimal/64-bit integer string transports and UTC date-time handling. Permission-gated UI is a convenience; enforce rules on the server. Include loading, empty, denied, validation and conflict states, and English/French message entries for module UI.

## 6. Make capabilities communicate safely

Within the modular monolith, use declared IntegrationEvents contracts or published named extension points; inspect the installed contracts before choosing a seam. Persist required business writes, audit and outbox events in the same transaction. Do not couple modules through private repositories or implementation projects. Inspect the [transactional outbox](adr/0010-transactional-outbox.md) before adding a consumer, and design idempotent handling; an event descriptor alone does not deliver or process an integration.

For an external service, specify the outbound contract, authorized data, server-only credentials, timeouts, idempotency/retry policy and failure recovery before implementing an adapter. The template cannot automatically connect an arbitrary API or supply your business rules. Keep credentials out of generated clients and review provider data policies. Updating the template/CLI does not retrofit existing applications or overwrite application-owned code.

## 7. Questions to ask next

Paste one at a time. Ask for source paths and verification evidence, not just a confident explanation.

- "Trace a Projects request from React or HTTP through authorization, Domain and PostgreSQL."
- "Explain how this application resolves Organization, User and Membership."
- "Show the actual enabled module versions, permissions and extension points using module facts."
- "Which layer should contain this business rule, and which tests would catch a violation?"
- "Help me specify my feature before generating a module; identify unsupported generator requirements."
- "Read .agents/skills/trykatch-build-module/SKILL.md and implement my approved feature brief, including its frontend only if installed."
- "How do I add a permission and safe default grants without bypassing server authorization?"
- "How do module manifests connect backend endpoints, OpenAPI clients, routes and navigation?"
- "How can two modules communicate through IntegrationEvents and the outbox without importing private implementations?"
- "How should I integrate this external API securely and handle duplicate deliveries or failure?"
- "How do I change persistence with a forward-only migration and verify cross-organization isolation?"
- "Read .agents/skills/trykatch-extend-module/SKILL.md and extend my existing module without overwriting custom source."
- "Read .agents/skills/trykatch-review/SKILL.md and review my task diff against its specification. Report findings without changing files."
- "Read .agents/skills/trykatch-verify/SKILL.md and run the checks appropriate to this change. Distinguish passed, failed, skipped and not-run checks."

## 8. Verify before calling it complete

Use [the verification matrix](development/verification.md) to select build, module/architecture, HTTP, real-PostgreSQL and frontend/browser checks. Generation success establishes its recorded checks, not every custom business rule or production deployment. Source changes after scaffolding need their affected checks again. Preserve failed diagnostics; never disable tests to make a claim green. Keep specifications and relevant guides aligned with the implementation.

This onboarding is for developers working in the repository. In-app AI Help remains a separate user-facing, read-only product feature with its existing authorization and provider configuration; onboarding does not enable it or grant it source-code access. Future application-specific multilingual help needs that product's approved guides and language verification, not an assumption that template architecture docs explain its business workflows.
