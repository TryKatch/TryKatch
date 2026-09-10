# AI-assisted development

Trykatch supports AI without putting a model inside the security kernel. There are two separate use cases.

## Faster module development

`AGENTS.md`, the module manifests, architectural decisions, generated OpenAPI document, and generated assistant contract give a coding agent a small, reliable context pack. An agent can discover the module seam, permission vocabulary, API operation IDs, React extension points, and verification commands without reverse-engineering the entire repository.

The practical workflow is:

1. describe the new business capability and its invariants;
2. add one backend module entry and one web module entry through the explicit registries;
3. implement domain, use case, adapter, endpoint, page, and extension contributions locally to that module;
4. build OpenAPI and regenerate the React client plus assistant contract;
5. run dependency, authorization, RLS, API, frontend, and disablement tests.

This makes AI useful for repeatable scaffolding and review while keeping architectural judgment and security rules in code and tests.

## Optional product assistants

`TrykatchAssistantToolDescriptor` is the only module seam for product-facing AI tools. The build converts allowlisted OpenAPI operations into strict function-tool schemas in `docs/generated/assistant-contract.json`. A future OpenAI Responses API adapter, MCP server, or another provider can translate model calls into those bindings.

The generated contract is not an authorization mechanism and does not contain credentials. Every call still goes through the normal API session, inferred organization context, permission handler, transaction-local PostgreSQL settings, and RLS. Do not execute a mutating model request without displaying the proposed action and receiving explicit confirmation.

Start with read-only retrieval operations. Add write tools only after idempotency, audit events, confirmation UX, rate limiting, evaluation cases, and failure recovery exist for that operation.

## Commands

```bash
dotnet build Trykatch.slnx
pnpm --dir web generate
pnpm --dir web generate:check
pnpm --dir web test
```

During development, browse `/docs` on the API origin for the interactive reference. The raw contract remains at `/openapi/v1.json`.
