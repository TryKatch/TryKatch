# AI-assisted development

Start with [developer onboarding](developer-onboarding.md) ([Français](developer-onboarding.fr.md)) for a read-only orientation prompt, architecture/module/backend/frontend/integration checklist and suggested questions for your existing coding assistant. This repository-based kit requires no separate AI provider key and leaves in-app AI Help unchanged.

Trykatch supports AI without putting a model inside the security kernel. There are two separate use cases.

## Faster module development

Every generated application includes [AGENTS.md](../AGENTS.md), five project-local skills under `.agents/skills/`, and focused guides under `docs/development/`. Together with manifests, architectural decisions, OpenAPI, and the assistant contract, they give a coding agent the module seam, permission vocabulary, API operation IDs, React extension points, and verification commands.

| Skill | Use it for |
| --- | --- |
| [trykatch-spec](../.agents/skills/trykatch-spec/SKILL.md) | Business rules, ownership, permissions, state transitions, and acceptance criteria |
| [trykatch-build-module](../.agents/skills/trykatch-build-module/SKILL.md) | A new module using the local CLI, a blueprint, and any remaining custom logic |
| [trykatch-extend-module](../.agents/skills/trykatch-extend-module/SKILL.md) | Existing module behavior and published extension points |
| [trykatch-review](../.agents/skills/trykatch-review/SKILL.md) | Findings against the request, architecture, authorization, and isolation |
| [trykatch-verify](../.agents/skills/trykatch-verify/SKILL.md) | Checks appropriate to the actual change, with explicit verification gaps |

Use your coding agent's project-skill discovery when supported. If it does not discover `.agents/skills/`, ask it to read the relevant `SKILL.md` directly; no global install, account connection, provider key, or additional CLI is needed for the bundled instructions. Your chosen coding agent still needs its own normal setup. Skills keep their `trykatch-*` names after application generation; C# namespaces and project paths in their instructions are renamed with the application.

For example:

> Read `.agents/skills/trykatch-build-module/SKILL.md` and build a shipment reception workflow using the existing shipment blueprint, including its React surface. Follow the specification through implementation and verification.

For a plan without implementation:

> Read `.agents/skills/trykatch-spec/SKILL.md` and specify equipment rentals with reservation availability. Identify the business decisions needed before implementation.

The practical workflow is:

1. Describe the capability and its invariants; keep substantial feature specifications in `docs/specs/` using the [specification guide](development/specifications.md).
2. Use the local module CLI for supported CRUD or blueprint workflows. It registers the module and runs its built-in checks.
3. Implement remaining domain, use case, adapter, endpoint, page, and extension behavior in the owning module.
4. Build OpenAPI; in React applications, regenerate the client and assistant contract.
5. Review against the request and use the [verification matrix](development/verification.md) for relevant architecture, authorization, RLS, API, frontend, and disablement checks.

This makes AI useful for repeatable scaffolding and review while keeping architectural judgment and security rules in code and tests.

The skills also ship in backend-only applications and skip frontend operations when `web/package.json` is absent. Generator success establishes the generated slice's checks, not every custom business rule. The agent must distinguish passed checks from unavailable prerequisites or untested acceptance criteria. Instructions do not guarantee model behavior and do not automatically commit, publish, or merge work.

## Optional product assistants

`AssistantToolDescriptor` is the module seam for product-facing AI tools. In React applications, the generation pipeline converts allowlisted OpenAPI operations into strict function-tool schemas in `docs/generated/assistant-contract.json`. A future model-provider adapter or MCP server can translate model calls into those bindings. No model runtime, chat UI, or approval executor is bundled by these development skills.

The generated contract is not an authorization mechanism and does not contain credentials. Every call still goes through the normal API session, inferred organization context, permission handler, transaction-local PostgreSQL settings, and RLS. Do not execute a mutating model request without displaying the proposed action and receiving explicit confirmation.

Start runtime integration with read-only retrieval operations. The current document-update descriptor declares confirmation metadata, but that flag does not implement an approval flow. Before enabling a write tool in an adapter, implement request-body schema/binding support, idempotency, audit events, confirmation UX, rate limiting, evaluation cases, and failure recovery for that operation.

## Commands

For a React application:

```bash
dotnet build Trykatch.slnx
corepack pnpm --dir web install --frozen-lockfile
corepack pnpm --dir web generate
corepack pnpm --dir web generate:check
corepack pnpm --dir web test
```

For backend-only output, build the solution and use the backend checks in the verification guide. Its OpenAPI document is emitted under the API project's `obj/openapi`; the frontend generation pipeline is absent.

During development, browse `/docs` on the API origin for the interactive reference. The raw contract remains at `/openapi/v1.json`.
