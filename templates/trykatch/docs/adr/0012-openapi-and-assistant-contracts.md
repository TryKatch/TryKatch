# ADR 0012: OpenAPI and assistant contracts

Status: Accepted

## Context

Trykatch modules need one API contract that serves human documentation, generated React clients, integration tests, and optional AI adapters. Giving a model every endpoint would turn the API surface into an accidental privilege boundary and would make provider changes expensive.

## Decision

ASP.NET Core generates OpenAPI 3.1 at build time. Stable operation IDs are mandatory. Scalar renders the development-only interactive reference at `/docs`; production does not expose documentation unless an operator deliberately adds an authenticated deployment policy.

Module-owned operations receive `x-trykatch-module` metadata. A module may expose an operation to assistants only by declaring a `TrykatchAssistantToolDescriptor`. The OpenAPI transformer emits that declaration as `x-trykatch-assistant-tool`, and the deterministic generator produces `docs/generated/assistant-contract.json`.

The assistant contract is provider-neutral and deny-by-default. The API remains the authority for identity, organization context, RLS, and permissions on every invocation. Read-only tools may run directly. Mutating and destructive tools must be explicitly declared and require human confirmation. Model providers are adapters outside the security kernel.

## Consequences

- OpenAPI changes drive the React client and assistant schema from the same source.
- AI adapters can consume strict function schemas without reflection or prompt-parsed endpoint lists.
- Installing or disabling a module changes its API documentation and assistant surface deterministically.
- A build fails when an assistant tool is unsafe or its generated contract drifts.
- Provider SDKs and credentials are optional; the base template remains useful without an AI account.
