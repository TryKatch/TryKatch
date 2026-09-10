# OpenMercato security and modularity comparison for Trykatch

Date: 2026-09-08

OpenMercato source reviewed at commit: [`30d509e`](https://github.com/open-mercato/open-mercato/tree/30d509eeb481ae4778be771d6c3992042add2d1a)

OpenMercato Agent Orchestrator branch reviewed at commit: [`4a01115`](https://github.com/open-mercato/open-mercato/tree/4a01115c065d0b758a9170ce5c2f3a128c76ee44)

Trykatch source reviewed at commit: `2a223d7`

## Executive decision

There are useful ideas in OpenMercato, but Trykatch should adopt the **contracts and discipline**, not copy its TypeScript implementation or security model.

The most valuable next improvements are:

1. a uniform command pipeline for every mutation, with module-owned validation, audit, outbox events, and constrained pre/post hooks;
2. typed module contracts for events, subscribers, workers, DataTable/form/detail extensions, and permission dependencies;
3. a server-side session registry with per-session revocation and security activity;
4. an optional field-encryption capability backed by an external KMS and explicit per-field policies;
5. richer module manifests, compatibility/provenance checks, and module-scoped migration/lifecycle diagnostics;
6. a clean-room, optional AgentOps module with permission-filtered tools, durable runs and proposals, explicit human approval, provider-neutral execution, OpenTelemetry, and deterministic evaluations.

Trykatch should **retain its stronger PostgreSQL RLS boundary, global-user/membership model, antiforgery protection, cookie-only first-party authentication, ASP.NET Core Identity/OpenIddict foundation, and non-overridable security kernel**.

## What the primary sources show

### Video review

The official OpenMercato site currently highlights three videos by Piotr Karwatka: [AI Assisted Engineering with Open Mercato](https://www.youtube.com/watch?v=GD2ToD1jmLE), [How to install Open Mercato](https://www.youtube.com/watch?v=OsalmbiWQ-I), and [Open Mercato Architecture](https://www.youtube.com/watch?v=ezfhcr_9Q0g). The architecture video is the relevant source for this comparison. Its embedded presentation reinforces the same design vocabulary as the written architecture page: a full-stack modular monolith, build-time overlays, dependency injection, a command/event request flow, PostgreSQL, RBAC, field-level encryption, and organization-scoped data access.

The videos do not expose English caption tracks through YouTube's public timed-text endpoint, so this note does not claim a verbatim transcript. Technical recommendations below are cross-checked against the versioned repository and official specifications rather than relying on the presentation alone. The video is useful for understanding intent; it is not independent evidence that every advertised mechanism is production-complete.

OpenMercato describes a modular monolith whose modules own UI, API, and schema contributions. It uses a core/overlay model and resolves extensions at build time. It also presents per-request DI, a command pattern for writes, events for cross-module side effects, field-level AES-GCM encryption, tenant-specific encryption context, and automatic tenant/organization query scoping. [Official architecture](https://www.openmercato.com/architecture)

The repository confirms that modules may contribute pages, routes, DI registrations, permissions, tenant setup, entities, migrations, events, subscribers, workers, extension hosts, response enrichers, API interceptors, DataTable/form extensions, and AI agents/tools. [`module-development.md`](https://github.com/open-mercato/open-mercato/blob/30d509eeb481ae4778be771d6c3992042add2d1a/.ai/docs/module-development.md)

Its Universal Module Extension System has implemented extension surfaces for menus, browser events, response enrichment, API interception, DataTable columns/actions/filters, form fields, component replacement, and query enrichment. The specification itself is still marked “In Progress,” so it is evidence of a useful direction rather than proof that every proposed phase is production-complete. [`SPEC-041`](https://github.com/open-mercato/open-mercato/blob/30d509eeb481ae4778be771d6c3992042add2d1a/.ai/specs/implemented/SPEC-041-2026-02-24-universal-module-extension-system.md)

OpenMercato’s open-source authentication code uses HttpOnly cookies containing a JWT plus an opaque session token, stores only a hash of the opaque token, rate-limits login and recovery, runs a dummy bcrypt comparison for unknown accounts, returns uniform invalid-credential responses, makes reset tokens single-use with an atomic compare-and-set, and revokes all sessions after a password reset. [`login.ts`](https://github.com/open-mercato/open-mercato/blob/30d509eeb481ae4778be771d6c3992042add2d1a/packages/core/src/modules/auth/api/login.ts), [`authService.ts`](https://github.com/open-mercato/open-mercato/blob/30d509eeb481ae4778be771d6c3992042add2d1a/packages/core/src/modules/auth/services/authService.ts), [`reset.ts`](https://github.com/open-mercato/open-mercato/blob/30d509eeb481ae4778be771d6c3992042add2d1a/packages/core/src/modules/auth/api/reset.ts)

Its RBAC model declares stable feature IDs in each module, applies role defaults during tenant setup, supports user-specific overrides, and resolves tenant/organization visibility as part of effective access. The project explicitly instructs new endpoints to authorize feature IDs rather than mutable role names. [`auth/AGENTS.md`](https://github.com/open-mercato/open-mercato/blob/30d509eeb481ae4778be771d6c3992042add2d1a/packages/core/src/modules/auth/AGENTS.md), [`acl.ts`](https://github.com/open-mercato/open-mercato/blob/30d509eeb481ae4778be771d6c3992042add2d1a/packages/core/src/modules/auth/acl.ts), [`rbacService.ts`](https://github.com/open-mercato/open-mercato/blob/30d509eeb481ae4778be771d6c3992042add2d1a/packages/core/src/modules/auth/services/rbacService.ts)

Its open-source encryption layer uses AES-256-GCM, configurable entity/field maps, tenant data-encryption keys, a Vault-backed KMS option, and deterministic keyed lookup hashes for searchable encrypted values. [`aes.ts`](https://github.com/open-mercato/open-mercato/blob/30d509eeb481ae4778be771d6c3992042add2d1a/packages/shared/src/lib/encryption/aes.ts), [`tenantDataEncryptionService.ts`](https://github.com/open-mercato/open-mercato/blob/30d509eeb481ae4778be771d6c3992042add2d1a/packages/shared/src/lib/encryption/tenantDataEncryptionService.ts), [`kms.ts`](https://github.com/open-mercato/open-mercato/blob/30d509eeb481ae4778be771d6c3992042add2d1a/packages/shared/src/lib/encryption/kms.ts)

OpenMercato’s assistant design is module-owned: an agent has a stable ID, required features, an exact tool allowlist, execution limits, and a mutation policy; runtime policy checks the actor’s features and rejects undeclared tools. Its mutation helper stages an expiring pending action rather than directly writing, carrying an idempotency key and optional record version for confirmation-time conflict checks. [`ai-agent-definition.ts`](https://github.com/open-mercato/open-mercato/blob/30d509eeb481ae4778be771d6c3992042add2d1a/packages/ai-assistant/src/modules/ai_assistant/lib/ai-agent-definition.ts), [`agent-policy.ts`](https://github.com/open-mercato/open-mercato/blob/30d509eeb481ae4778be771d6c3992042add2d1a/packages/ai-assistant/src/modules/ai_assistant/lib/agent-policy.ts), [`prepare-mutation.ts`](https://github.com/open-mercato/open-mercato/blob/30d509eeb481ae4778be771d6c3992042add2d1a/packages/ai-assistant/src/modules/ai_assistant/lib/prepare-mutation.ts)

Licensing needs a clear boundary. The repository root is MIT-licensed, but the `@open-mercato/enterprise` package is proprietary and expressly prohibits reproduction, modification, reverse engineering, derivative work, production use, and commercial use without a license. MFA, passkeys, enterprise SSO/SCIM, login interceptors, and several security overlays are listed as commercial modules. Trykatch must not derive implementation from that package. [Root MIT license](https://github.com/open-mercato/open-mercato/blob/30d509eeb481ae4778be771d6c3992042add2d1a/LICENSE), [enterprise license](https://github.com/open-mercato/open-mercato/blob/30d509eeb481ae4778be771d6c3992042add2d1a/packages/enterprise/LICENSE.md), [enterprise capability list](https://github.com/open-mercato/open-mercato/blob/30d509eeb481ae4778be771d6c3992042add2d1a/packages/enterprise/README.md#enterprise-software-package)

## Comparison and recommendation

| Area | OpenMercato evidence | Trykatch position | Decision |
| --- | --- | --- | --- |
| Full-stack modules | Build-time discovery of UI, API, schema, setup, workers, and AI contributions. [`module-development.md`](https://github.com/open-mercato/open-mercato/blob/30d509eeb481ae4778be771d6c3992042add2d1a/.ai/docs/module-development.md) | Explicit backend and React catalogs, dependency validation, lazy routes, named extension points, module-owned permissions, EF model contributors, and assistant-tool metadata already exist. [`modules.md`](../../templates/trykatch/docs/modules.md) | **Already present.** Continue with explicit/source-generated catalogs; do not replace them with runtime scanning. |
| Tenant isolation | Application/ORM query scoping injects `tenantId` and `organizationId`. [Official architecture](https://www.openmercato.com/architecture) | Actor and organization are resolved server-side; transaction-local PostgreSQL settings activate application predicates plus RLS. [`OrganizationScopeMiddleware.cs`](../../templates/trykatch/src/API/Trykatch.Api/Security/OrganizationScopeMiddleware.cs), [`OrganizationTransactionMiddleware.cs`](../../templates/trykatch/src/API/Trykatch.Api/Security/OrganizationTransactionMiddleware.cs) | **Keep Trykatch.** Application filters are useful ergonomics but are not an adequate replacement for forced PostgreSQL RLS. |
| Identity boundary | Open-source core uses tenant-bound user records and tenant-scoped email uniqueness. [`entities.ts`](https://github.com/open-mercato/open-mercato/blob/30d509eeb481ae4778be771d6c3992042add2d1a/packages/core/src/modules/auth/data/entities.ts) | Trykatch uses global identities with organization memberships and separates platform access from organization RBAC. | **Keep Trykatch.** One human should not need duplicate credentials for every organization. |
| Browser authentication | HttpOnly `SameSite=Lax` cookies, JWT plus hashed session token; login JSON can also return bearer/refresh tokens. [`login.ts`](https://github.com/open-mercato/open-mercato/blob/30d509eeb481ae4778be771d6c3992042add2d1a/packages/core/src/modules/auth/api/login.ts) | First-party React uses a `__Host-` Secure HttpOnly `SameSite=Strict` cookie and antiforgery validation; bearer and refresh tokens are not returned to React. [`DependencyInjection.cs`](../../templates/trykatch/src/Common/Trykatch.Identity/DependencyInjection.cs), [`AuthenticationController.cs`](../../templates/trykatch/src/API/Trykatch.Api/Controllers/AuthenticationController.cs) | **Keep Trykatch.** Do not expose first-party tokens to JavaScript. |
| Session lifecycle | Hashed opaque session rows allow refresh-token revocation; the JWT session ID is rechecked against the current user, tenant, organization, and roles; reset revokes all user sessions. [`authService.ts`](https://github.com/open-mercato/open-mercato/blob/30d509eeb481ae4778be771d6c3992042add2d1a/packages/core/src/modules/auth/services/authService.ts), [`sessionIntegrity.ts`](https://github.com/open-mercato/open-mercato/blob/30d509eeb481ae4778be771d6c3992042add2d1a/packages/core/src/modules/auth/lib/sessionIntegrity.ts) | ASP.NET Identity security stamps invalidate cookies after password, MFA, and access changes, but there is no first-class device/session inventory. [`DependencyInjection.cs`](../../templates/trykatch/src/Common/Trykatch.Identity/DependencyInjection.cs) | **Adopt the property, not the JWT design.** Add an application-session registry with hashed opaque identifiers, issued/last-seen/expiry/revoked timestamps, coarse device metadata, “sign out this session,” and “sign out all other sessions.” Keep the Identity cookie as the browser credential. |
| Login and recovery hardening | Rate limits, uniform responses, timing equalization, hashed reset tokens, atomic single use, and session revocation. [`login.ts`](https://github.com/open-mercato/open-mercato/blob/30d509eeb481ae4778be771d6c3992042add2d1a/packages/core/src/modules/auth/api/login.ts), [`reset.ts`](https://github.com/open-mercato/open-mercato/blob/30d509eeb481ae4778be771d6c3992042add2d1a/packages/core/src/modules/auth/api/reset.ts) | Confirmed email, lockout, generic recovery response, rate limiting, Identity token providers, MFA, recovery codes, antiforgery, and security-stamp invalidation are implemented and integration-tested. [`AuthenticationSecurityTests.cs`](../../templates/trykatch/tests/Trykatch.IntegrationTests/AuthenticationSecurityTests.cs) | **Already present, with one refinement.** Add a timing-equalized password-verification test for unknown users if ASP.NET Identity’s path does not already guarantee equivalent work. |
| MFA and enterprise SSO | MFA/passkeys and SSO/SCIM are proprietary OpenMercato modules. [Enterprise package](https://github.com/open-mercato/open-mercato/blob/30d509eeb481ae4778be771d6c3992042add2d1a/packages/enterprise/README.md#enterprise-software-package) | TOTP/recovery codes use free ASP.NET Identity; OpenIddict supplies the authorization-code + PKCE and client-credentials protocol foundation. External IdP and SCIM lifecycle composition are not complete. | **Do not copy.** Implement standards-based external OIDC and optional SCIM independently with free .NET packages and protocol tests. Keep this behind stable identity-provider/provisioning interfaces. |
| RBAC | Stable module feature IDs, module-owned defaults, explicit permission dependencies, role grants, user overrides, and organization visibility. [`auth/AGENTS.md`](https://github.com/open-mercato/open-mercato/blob/30d509eeb481ae4778be771d6c3992042add2d1a/packages/core/src/modules/auth/AGENTS.md), [`directory/acl.ts`](https://github.com/open-mercato/open-mercato/blob/30d509eeb481ae4778be771d6c3992042add2d1a/packages/core/src/modules/directory/acl.ts) | Stable code-defined permissions, module providers/default grants, custom organization roles, and separate platform roles already exist. [`Permissions.cs`](../../templates/trykatch/src/Common/Trykatch.Application/Authorization/Permissions.cs) | **Adopt selectively.** Add permission dependency/conflict diagnostics and an “effective access” explanation. Avoid general per-user grants initially; they make access difficult to review. Use scoped role assignments or time-bound elevation instead. |
| Field-level protection | Configurable AES-GCM field encryption with tenant DEKs, KMS adapters, and keyed lookup hashes. [`aes.ts`](https://github.com/open-mercato/open-mercato/blob/30d509eeb481ae4778be771d6c3992042add2d1a/packages/shared/src/lib/encryption/aes.ts), [`kms.ts`](https://github.com/open-mercato/open-mercato/blob/30d509eeb481ae4778be771d6c3992042add2d1a/packages/shared/src/lib/encryption/kms.ts) | PostgreSQL encryption at rest is deployment-owned; Data Protection secures framework tokens/keys, but Trykatch has no generic module field-encryption contract. | **Adopt as optional capability.** Define field policies in the module descriptor, envelope-encrypt with AES-GCM, keep keys outside PostgreSQL, support rotation/versioning, and use keyed blind indexes only where equality lookup is required. Do not encrypt every column by default. |
| Commands, audit, and events | Every write is intended to use a command; side effects use events; this supports audit and undo/redo. [Official architecture](https://www.openmercato.com/architecture) | Focused use cases, transactional audit/outbox, and correlated delivery are present, but module-owned command/interceptor/subscriber/worker contracts are incomplete. [`0010-transactional-outbox.md`](../../templates/trykatch/docs/adr/0010-transactional-outbox.md) | **Adopt.** Create one mutation pipeline and module contracts for events/subscribers/workers. Hooks may validate, enrich, or veto but cannot skip authorization, organization resolution, RLS, audit, or outbox atomicity. |
| UI extension system | Named extension points cover menus, tables, forms, components, enrichers, and interceptors. [`SPEC-041`](https://github.com/open-mercato/open-mercato/blob/30d509eeb481ae4778be771d6c3992042add2d1a/.ai/specs/implemented/SPEC-041-2026-02-24-universal-module-extension-system.md) | Trykatch has routes, navigation, a generic extension host, keyed overrides, and catalog validation. [`module-sdk`](../../templates/trykatch/web/packages/module-sdk/src/index.ts) | **Adopt and narrow.** Add purpose-specific contracts for DataTable columns/filters/actions, forms, details, dashboard widgets, command palette, and notifications. Only the application may replace components; modules should normally extend named slots. |
| Build-time overlay | Convention scanning lets an app overlay or replace core files. [Official architecture](https://www.openmercato.com/architecture) | Explicit catalogs avoid reflection and make ownership visible. | **Adapt, do not copy.** Generate typed registries and explicit keyed overrides from manifests. Never use filename precedence as the only conflict rule. Duplicate routes, permission IDs, slots, or package versions should fail the build. |
| DI overrides | Per-request DI can replace a service registration. [Official architecture](https://www.openmercato.com/architecture) | .NET services are composed explicitly at startup. | **Avoid general service replacement.** Publish small provider interfaces for intended variability. Security services must remain host-owned, decorated only through constrained policies, and validated at startup. |
| AI-assisted modules | Module-owned agents/tools, exact allowlists, required permissions, execution limits, mutation policy, and staged pending actions with idempotency/version checks. [`ai-agent-definition.ts`](https://github.com/open-mercato/open-mercato/blob/30d509eeb481ae4778be771d6c3992042add2d1a/packages/ai-assistant/src/modules/ai_assistant/lib/ai-agent-definition.ts), [`prepare-mutation.ts`](https://github.com/open-mercato/open-mercato/blob/30d509eeb481ae4778be771d6c3992042add2d1a/packages/ai-assistant/src/modules/ai_assistant/lib/prepare-mutation.ts) | Modules expose a default-deny assistant tool allowlist through OpenAPI, including risk and confirmation metadata; a runtime executor is not yet present. [`0012-openapi-and-assistant-contracts.md`](../../templates/trykatch/docs/adr/0012-openapi-and-assistant-contracts.md) | **Adopt next.** Add permission-filtered runtime discovery, provider-neutral execution adapters, time/token/tool-call budgets, per-call reauthorization, trace/audit attributes, and pending-action approval for all writes. At approval, revalidate actor, organization, permission, record version, expiry, and idempotency. Tenant configuration may narrow policy, never widen it. |
| AI-readable engineering | Module-level `AGENTS.md`, specs, lessons, and validation commands constrain coding agents. [`AGENTS.md`](https://github.com/open-mercato/open-mercato/blob/30d509eeb481ae4778be771d6c3992042add2d1a/AGENTS.md) | Trykatch has a repository-level `AGENTS.md`, ADRs, module docs, and deterministic validation commands. [`AGENTS.md`](../../templates/trykatch/AGENTS.md) | **Adopt.** Give every distributable module a concise machine-readable contract: ownership, allowed dependencies, security invariants, extension IDs, and exact verification commands. Add CI validation for manifest/code/doc drift. |

## Agent Orchestrator addendum

### Licensing boundary — non-negotiable

OpenMercato markets Agent Orchestrator as an Enterprise feature, and its repository lists the module under the proprietary `@open-mercato/enterprise` package. That package is free only for non-production developer evaluation and expressly prohibits production or commercial use, reproduction, redistribution, modification, reverse engineering, derivation, or generating new features based on the package without a commercial license. [Agent Orchestrator product page](https://www.openmercato.com/agent-orchestrator), [enterprise capability list](https://github.com/open-mercato/open-mercato/blob/4a01115c065d0b758a9170ce5c2f3a128c76ee44/packages/enterprise/README.md), [enterprise license](https://github.com/open-mercato/open-mercato/blob/4a01115c065d0b758a9170ce5c2f3a128c76ee44/packages/enterprise/LICENSE.md)

**Trykatch must therefore implement only independently designed, standards-based properties.** The links below establish architectural evidence and risks; they are not permission to port source, schemas, tests, UI, or wording. Any future implementation team should work from this decision record, public standards, Microsoft/.NET contracts, and Trykatch's own tests—not from the proprietary package source.

### Product claims versus the reviewed implementation

The public page advertises in-process Vercel AI SDK agents, OpenCode agents in a self-hosted container, and A2A-connected cloud runtimes; workflow pause/resume; confidence-based human escalation; MCP tools; two-layer audit; evals; and model, token-cost, and performance observability. These are vendor product claims, not independent proof of production maturity. [Agent Orchestrator product page](https://www.openmercato.com/agent-orchestrator)

The reviewed branch documents a narrower implemented baseline: one registry and run contract with two runtimes (`in-process` and `opencode`), typed informative/actionable results, persisted runs and proposals, a disposition step, and later execution through an audited command. Its roadmap separately lists identity, context, guardrails, trace/eval, A2A/dispatch, lifecycle, compliance, and retention work; no A2A adapter was visible in the reviewed package tree at commit `4a01115`. Trykatch should treat A2A as a later interoperability adapter, not a v1 dependency. [Implemented baseline](https://github.com/open-mercato/open-mercato/blob/4a01115c065d0b758a9170ce5c2f3a128c76ee44/.ai/specs/enterprise/agent-orchestrator/00-IMPLEMENTED-BASELINE.md), [specification index and roadmap](https://github.com/open-mercato/open-mercato/blob/4a01115c065d0b758a9170ce5c2f3a128c76ee44/.ai/specs/enterprise/agent-orchestrator/README.md)

### Concrete architecture and Trykatch decision

| Concern | Primary-source evidence | Clean-room Trykatch decision |
| --- | --- | --- |
| Execution contract | OpenMercato's baseline uses a typed `informative` or `actionable` result. An actionable result persists a proposal; the agent does not perform the domain write, and an approved proposal is later executed through an audited command. [Implemented baseline](https://github.com/open-mercato/open-mercato/blob/4a01115c065d0b758a9170ce5c2f3a128c76ee44/.ai/specs/enterprise/agent-orchestrator/00-IMPLEMENTED-BASELINE.md) | **Adopt the property.** Define Trykatch `AgentRun`, typed `AgentOutcome`, `ActionProposal`, `ApprovalDecision`, and `ActionExecution` contracts. Start read-only. All writes remain normal application commands and must pass current authorization, organization scope, validation, RLS, audit, and outbox at execution time. |
| Provider abstraction | The reviewed implementation places two runtimes behind one registry/run interface. Microsoft's provider-neutral .NET layer exposes `IChatClient`, middleware, tool invocation, telemetry, caching, and test doubles across providers. [Implemented baseline](https://github.com/open-mercato/open-mercato/blob/4a01115c065d0b758a9170ce5c2f3a128c76ee44/.ai/specs/enterprise/agent-orchestrator/00-IMPLEMENTED-BASELINE.md), [Microsoft.Extensions.AI](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai) | **Adapt for .NET.** Put `IChatClient` behind a Trykatch-owned runtime interface and optional provider adapters. Keep agent definitions, authorization, budgets, persistence, and approval independent of any model vendor. Do not put an experimental agent framework in the security kernel; Microsoft's .NET AI ecosystem describes higher-level agent frameworks separately from the stable provider abstraction. [.NET AI ecosystem](https://learn.microsoft.com/en-us/dotnet/ai/dotnet-ai-ecosystem), [Semantic Kernel agent architecture status](https://learn.microsoft.com/en-us/semantic-kernel/frameworks/agent/agent-architecture) |
| Identity and delegation | OpenMercato models an agent as a non-interactive, organization-scoped principal and documents short-lived, audience-bound tokens, client credentials or signed assertion, scope intersection, current grant checks, and optional on-behalf-of attribution. [Agent authentication](https://github.com/open-mercato/open-mercato/blob/4a01115c065d0b758a9170ce5c2f3a128c76ee44/packages/enterprise/src/modules/agent_orchestrator/auth.md) | **Adopt the boundary.** Give each deployed agent a first-class service principal, explicit organization grants, expiry/revocation, and optional initiating-human ID. Use OpenIddict/OAuth client credentials or token exchange rather than inventing a proprietary signed-token format. Never let an agent select its own organization or widen the human's access. |
| Tool and permission scope | The baseline strips mutation tools from propose-only agents and rechecks permissions at tool calls; execution revalidates proposed actions against allowed server vocabulary immediately before dispatch. [Implemented baseline](https://github.com/open-mercato/open-mercato/blob/4a01115c065d0b758a9170ce5c2f3a128c76ee44/.ai/specs/enterprise/agent-orchestrator/00-IMPLEMENTED-BASELINE.md), [proposal execution](https://github.com/open-mercato/open-mercato/blob/4a01115c065d0b758a9170ce5c2f3a128c76ee44/packages/enterprise/src/modules/agent_orchestrator/lib/runtime/executeProposal.ts) | **Adopt with defense in depth.** Effective tools are the intersection of the host registry, agent allowlist, organization policy, and the current principal's permissions. Recompute that set on every call and again before an effect. Treat MCP tool annotations as descriptive metadata, never as authorization; MCP requires servers to validate inputs and clients to show tool use and obtain confirmation for sensitive operations. [MCP tools](https://modelcontextprotocol.io/specification/2025-11-25/server/tools), [MCP security and trust principles](https://modelcontextprotocol.io/specification/2025-03-26/index) |
| Human approval | The current policy states that confidence is evidence, not authorization. Auto-approval fails closed on policy, guardrails, trace completeness, declared risk, confidence threshold, and ambiguity; otherwise it creates a human task. Disposition is approve/edit/reject with optimistic concurrency and idempotent same-verdict handling. [Auto-approval policy](https://github.com/open-mercato/open-mercato/blob/4a01115c065d0b758a9170ce5c2f3a128c76ee44/packages/enterprise/src/modules/agent_orchestrator/lib/disposition/autoApprovalPolicy.ts), [disposition service](https://github.com/open-mercato/open-mercato/blob/4a01115c065d0b758a9170ce5c2f3a128c76ee44/packages/enterprise/src/modules/agent_orchestrator/lib/disposition/dispositionService.ts), [disposition command](https://github.com/open-mercato/open-mercato/blob/4a01115c065d0b758a9170ce5c2f3a128c76ee44/packages/enterprise/src/modules/agent_orchestrator/commands/dispose.ts) | **Adopt a stricter initial policy.** Trykatch defaults every mutation to `alwaysAsk`; high-risk identity, permission, billing, deletion, secret, and organization actions can never auto-approve. A confidence number supplied by a model is never authority. Approval records the proposed diff, risk, evidence, approver, reason, expiry, and record versions; execution reauthorizes and rejects stale proposals. |
| Context and guardrails | The reviewed code redacts declared encrypted/PII fields before context packing, attaches provenance, budgets context, checks schema and tool scope, and uses a cite-or-abstain grounding rule. Its prompt-injection detector explicitly calls itself a heuristic signal rather than a security boundary. [Redactor](https://github.com/open-mercato/open-mercato/blob/4a01115c065d0b758a9170ce5c2f3a128c76ee44/packages/enterprise/src/modules/agent_orchestrator/lib/context/redactor.ts), [context packer](https://github.com/open-mercato/open-mercato/blob/4a01115c065d0b758a9170ce5c2f3a128c76ee44/packages/enterprise/src/modules/agent_orchestrator/lib/context/packer.ts), [guardrail service](https://github.com/open-mercato/open-mercato/blob/4a01115c065d0b758a9170ce5c2f3a128c76ee44/packages/enterprise/src/modules/agent_orchestrator/lib/guardrails/guardrailService.ts), [prompt-injection signal](https://github.com/open-mercato/open-mercato/blob/4a01115c065d0b758a9170ce5c2f3a128c76ee44/packages/enterprise/src/modules/agent_orchestrator/lib/guardrails/promptInjection.ts), [grounding check](https://github.com/open-mercato/open-mercato/blob/4a01115c065d0b758a9170ce5c2f3a128c76ee44/packages/enterprise/src/modules/agent_orchestrator/lib/guardrails/grounding.ts) | **Adopt.** Modules declare context field allowlists, sensitivity, provenance, and maximum sizes. Redact before provider calls; require evidence references or abstention for business decisions. Injection classifiers may block or escalate, but only tool scoping, authorization, propose-only execution, and RLS are security boundaries. |
| Sandboxing | File-defined helper scripts run in a fresh `isolated-vm` context with no Node globals, filesystem, network, `fetch`, or `process`, plus memory and time limits. The script source is trusted, committed repository code; the model selects a named script rather than providing source. [Sandboxed script runner](https://github.com/open-mercato/open-mercato/blob/4a01115c065d0b758a9170ce5c2f3a128c76ee44/packages/enterprise/src/modules/agent_orchestrator/lib/runtime/sandboxedScript.ts), [Implemented baseline](https://github.com/open-mercato/open-mercato/blob/4a01115c065d0b758a9170ce5c2f3a128c76ee44/.ai/specs/enterprise/agent-orchestrator/00-IMPLEMENTED-BASELINE.md) | **Adapt conservatively.** Do not execute model-authored code in the API process. A managed isolate is acceptable only for reviewed, pure helper code. If arbitrary code execution is ever added, run it in an ephemeral worker/container or Wasmtime sandbox with no filesystem, network, or secrets by default, explicit egress, CPU/memory/time/output limits, and a kill switch. |
| Durable state | The implementation persists runs, proposals, runtime/external IDs, parent/invocation IDs, model usage, cost, and outcomes; workflows persist instances, events, and human tasks. Its OpenCode handoff uses a database session row keyed by a per-run token and deletes it in `finally`. [Persistence](https://github.com/open-mercato/open-mercato/blob/4a01115c065d0b758a9170ce5c2f3a128c76ee44/packages/enterprise/src/modules/agent_orchestrator/lib/runtime/persistence.ts), [workflow architecture](https://github.com/open-mercato/open-mercato/blob/4a01115c065d0b758a9170ce5c2f3a128c76ee44/apps/docs/docs/framework/workflows/architecture.mdx), [run-session store](https://github.com/open-mercato/open-mercato/blob/4a01115c065d0b758a9170ce5c2f3a128c76ee44/packages/enterprise/src/modules/agent_orchestrator/lib/runtime/agentRunSessionStore.ts) | **Adopt durable state, improve credential handling.** Use PostgreSQL rows and the transactional outbox for `queued → running → waiting_approval → completed/failed/cancelled`, idempotency keys, leases, optimistic concurrency, retry count, and cancellation. Persist only a digest of any callback/session secret with an expiry and cleanup job; a crash must not leave a reusable raw credential. |
| Observability and evals | The reviewed code captures model/tool spans, usage and cost, idempotent trace ingestion, artifact caps, correction records, and rollups such as latency, error rate, eval pass rate, corrections, and approve-unchanged rate. Deterministic gates and golden matches are distinguished from an LLM judge, and the judge is not allowed to gate CI. [Native trace capture](https://github.com/open-mercato/open-mercato/blob/4a01115c065d0b758a9170ce5c2f3a128c76ee44/packages/enterprise/src/modules/agent_orchestrator/lib/runtime/nativeTraceCapture.ts), [trace ingestion](https://github.com/open-mercato/open-mercato/blob/4a01115c065d0b758a9170ce5c2f3a128c76ee44/packages/enterprise/src/modules/agent_orchestrator/lib/trace/traceIngestionService.ts), [metric rollups](https://github.com/open-mercato/open-mercato/blob/4a01115c065d0b758a9170ce5c2f3a128c76ee44/packages/enterprise/src/modules/agent_orchestrator/lib/metrics/metricRollupService.ts), [eval gate](https://github.com/open-mercato/open-mercato/blob/4a01115c065d0b758a9170ce5c2f3a128c76ee44/packages/enterprise/src/modules/agent_orchestrator/lib/eval/evalGate.ts), [continuous eval specification](https://github.com/open-mercato/open-mercato/blob/4a01115c065d0b758a9170ce5c2f3a128c76ee44/.ai/specs/enterprise/agent-orchestrator/2026-07-25-continuous-online-eval-and-golden-match.md) | **Adopt through Trykatch's existing OpenTelemetry stack.** Trace agent, workflow, model, and tool spans; record provider/model, tokens, latency, estimated cost, tool/permission decision, approval outcome, and run ID using OpenTelemetry GenAI semantic conventions. Prompt/output content is off by default because the semantic conventions identify content fields as potentially sensitive. [OpenTelemetry GenAI attributes](https://opentelemetry.io/docs/specs/semconv/registry/attributes/gen-ai/) Durable evidence required by approval must commit before evaluation; it cannot rely on best-effort trace export. |
| Scaling and failure recovery | Official deployment guidance requires a durable queue and dedicated workers for production, with concurrency, timeout, retry/backoff, database-pool, and provider-budget controls; the source's admission and provider budgets are process-local. [Scaling guide](https://github.com/open-mercato/open-mercato/blob/4a01115c065d0b758a9170ce5c2f3a128c76ee44/apps/docs/docs/deployment/agent-orchestration-scaling.mdx), [admission control](https://github.com/open-mercato/open-mercato/blob/4a01115c065d0b758a9170ce5c2f3a128c76ee44/packages/enterprise/src/modules/agent_orchestrator/lib/runtime/admission.ts), [provider budget](https://github.com/open-mercato/open-mercato/blob/4a01115c065d0b758a9170ce5c2f3a128c76ee44/packages/enterprise/src/modules/agent_orchestrator/lib/runtime/providerBudget.ts) | **Adapt to Trykatch's smaller default.** Start with a PostgreSQL-backed background worker and bounded concurrency; make Redis or a broker an optional scale adapter rather than a kernel dependency. Enforce hard ceilings for wall time, model tokens, tool calls, output bytes, retries, and estimated cost. Add fleet-wide quotas only when multi-instance load requires them. |
| MCP and A2A interoperability | MCP standardizes tools and OAuth-based authorization but leaves each server responsible for validation and access control. A2A describes agent discovery through Agent Cards and enterprise authentication while requiring implementations to authorize the requested skill, action, and data. [MCP tools](https://modelcontextprotocol.io/specification/2025-11-25/server/tools), [MCP authorization](https://modelcontextprotocol.io/specification/2025-11-25/basic/authorization), [A2A specification](https://a2a-protocol.org/dev/specification/) | **Add as optional edge adapters only.** Expose the same reviewed Trykatch tool registry and authorization engine through MCP; never create a second permission system. Add A2A only after the local runtime passes security, durability, and conformance tests. Agent Cards and remote metadata are discovery input, not authority. |

One historical OpenMercato issue is especially relevant: an “all organizations” selection could persist an agent run/proposal against a phantom organization ID and orphan human review. The issue is closed, so it is evidence of a failure class rather than a current defect. Trykatch must require a concrete, server-validated organization before any organization-scoped invocation, enforce a real foreign key plus forced RLS, and prohibit aggregate organization scopes from creating proposals or effects. [OpenMercato issue #3629](https://github.com/open-mercato/open-mercato/issues/3629)

### Trykatch AgentOps boundary

```text
Trykatch security kernel (existing, non-replaceable)
  Identity + service principals + sessions
  Organization resolution + membership
  Permission enforcement + PostgreSQL RLS
  Command pipeline + audit + outbox
                 │
                 ▼
Optional Trykatch.AgentOps module
  Agent catalog + versioned definitions
  Provider-neutral runtime (IChatClient adapters)
  Context builder + redaction + provenance
  Tool policy intersection + execution budgets
  Durable runs + typed proposals + approval inbox
  Deterministic evals + OpenTelemetry
          │                    │
          ├── module agents    ├── optional MCP adapter
          ├── module tools     └── later A2A adapter
          └── reviewed skills
```

AgentOps is a normal optional module, not a privileged parallel platform. It may request data and submit proposals only through security-kernel interfaces. It cannot bypass the active organization, forced RLS, permission checks, command validation, audit, or outbox.

### Security invariants and verification gates

- An agent invocation has one concrete organization, one agent principal, and—when delegated—one initiating human; all three identifiers flow through logs, traces, audit, proposals, and effects.
- The model never supplies executable source, permission names, organization IDs, tool authority, or approval state. It may select only versioned, host-registered tool and action IDs.
- Read operations are still permission-checked and RLS-scoped. Writes are proposals until a separately authorized application command executes them.
- Approval is not execution. Execution rechecks organization membership, agent grant, approver authority, permission, proposal expiry, record version, policy version, and idempotency.
- Secrets, raw tokens, passwords, Data Protection material, full prompts, and full outputs never enter ordinary logs or traces. Optional encrypted artifacts require a retention classification and access audit.
- A failed, timed-out, cancelled, or restarted run cannot silently continue, duplicate a command, or consume unbounded provider budget.

The release gate for the optional module should include unit tests for the run state machine, schemas, tool-intersection policy, risk policy, budgets, and context redaction; PostgreSQL integration tests for cross-organization RLS and restart recovery; concurrency tests for duplicate dispositions and stale proposals; adversarial tests for prompt injection, tool smuggling, SSRF, oversized output, and secret exfiltration; provider-contract tests using a fake `IChatClient`; deterministic replay/eval tests that cannot execute writes; observability tests for one correlated trace without sensitive content; and load tests for fairness, concurrency, cancellation, and cost ceilings. The OpenMercato baseline itself enumerates registry, ACL, propose-only, sandbox, schema, session, and workflow-resume tests, while its eval gate keeps stochastic judging out of CI decisions. [Implemented baseline](https://github.com/open-mercato/open-mercato/blob/4a01115c065d0b758a9170ce5c2f3a128c76ee44/.ai/specs/enterprise/agent-orchestrator/00-IMPLEMENTED-BASELINE.md), [eval gate](https://github.com/open-mercato/open-mercato/blob/4a01115c065d0b758a9170ce5c2f3a128c76ee44/packages/enterprise/src/modules/agent_orchestrator/lib/eval/evalGate.ts)

## Clean-room target architecture

```text
Trykatch security kernel (host-owned, non-replaceable)
  Identity / cookies / antiforgery / OpenIddict
  Actor + organization resolution
  Permission authorization
  PostgreSQL transaction context + forced RLS
  Audit + transactional outbox
  Module validation + provenance
           │
           ├── Typed module contracts
           │     API endpoints and OpenAPI
           │     EF model + migrations
           │     permissions + setup
           │     commands + events + subscribers + workers
           │     React routes + named UI contributions
           │     optional AgentOps agents/tools/skills
           │
           └── Optional security providers
                 external OIDC / SCIM
                 email/SMS delivery
                 field encryption / KMS
                 storage malware scanning
                 model providers / MCP / A2A
```

The host should invoke a fixed pipeline:

```text
authenticate actor
  → resolve active organization and revalidate membership
  → authorize stable permission
  → validate command
  → run constrained module guards
  → open transaction and set app.organization_id / app.actor_id
  → execute use case
  → write audit snapshot + outbox events atomically
  → commit
  → deliver idempotent module subscribers/workers
```

An extension can add validation or deny an action. It cannot choose the actor or organization, grant itself permission, suppress audit/outbox writes, change RLS settings, or execute a mutation outside this pipeline.

## Prioritized changes

### P0 — security and module integrity

- Add manifest contract version, host compatibility range, package identity/provenance, backend/web pairing, and declared contribution inventory.
- Fail CI on backend/web module ID, version, permission, route, extension-host, or assistant-tool drift.
- Add module-owned event, subscriber, and worker descriptors with stable IDs, organization-scope rules, retry/concurrency policy, idempotency strategy, and telemetry requirements.
- Add purpose-specific frontend extension contracts for tables, forms, details, dashboards, commands, and notifications.
- Add effective-access diagnostics that explain which role supplied each permission and which organization boundary applies.
- Record the AgentOps clean-room boundary in an ADR: agents are propose-only, the security kernel is non-replaceable, mutations default to human approval, and one concrete server-validated organization is mandatory.
- Extend module manifests with versioned agent, tool, skill, action, context-field, risk, and provider-capability declarations; fail build and startup on unknown IDs, duplicates, undeclared mutations, incompatible versions, or frontend/backend drift.

### P1 — authentication and sensitive data

- Add application session records and a profile “Sessions” page for per-device revocation and global sign-out.
- Add independently designed external OIDC provider adapters and lifecycle tests; consider SCIM only when there is a real directory-provisioning requirement.
- Add optional field-encryption abstractions, one local development provider, one external KMS provider, rotation/version metadata, and an RLS-aware integration test proving ciphertext and cross-organization isolation.
- Add a security-event vocabulary for login success/failure, lockout, password reset, MFA enrollment/reset, session revocation, role changes, invitation acceptance, and organization-context changes.

### P2 — optional AgentOps foundation

- Generate deterministic backend and React catalogs from a reviewed module manifest without runtime assembly loading.
- Add module migration status, compatibility checks, `doctor`, disable, upgrade, eject, and safe remove operations; removal retains data unless a separate explicit purge is authorized.
- Add a provider-neutral runtime around `IChatClient`, a fake provider for tests, versioned read-only agent definitions, per-call tool-policy intersection, and hard time/token/tool/output/cost budgets.
- Add PostgreSQL-backed run/proposal state, leases, idempotency, cancellation, restart recovery, an approval inbox, and audited command execution. Persist digests rather than raw callback/session tokens.
- Add OpenTelemetry GenAI spans and low-cardinality metrics; keep prompt/output content disabled by default. Add deterministic schema/policy/golden-case evals; never make an LLM judge a release gate.
- Pilot one read-only Project agent and one always-ask Project update proposal. Do not enable autonomous identity, permission, tenant, secret, billing, deletion, or destructive actions.
- Add module-level `AGENTS.md` files and a generator that emits a module contract sheet for developers and coding agents.

### P3 — context, interoperability, and scale

- Add module-owned context field allowlists, PII redaction, provenance, citation/abstention, retention classes, encrypted evidence artifacts, and adversarial prompt/tool tests.
- Expose reviewed read-only tools through an optional MCP adapter backed by the same authorization engine; add OAuth audience/scope tests and explicit user-consent UX.
- Add A2A only after the local runtime is stable, using the published protocol with discovery treated as untrusted input and authorization enforced locally.
- Add optional distributed queue/provider-quota adapters for multi-instance workloads. Keep PostgreSQL plus the outbox as the default deployment until measured load requires another dependency.
- Consider narrowly scoped auto-approval only after production evidence exists. It must be action-specific, low-risk, policy-enabled, trace-complete, unambiguous, revocable, and never based on confidence alone.

## Explicitly avoid

- Do not replace PostgreSQL RLS with automatic ORM filters.
- Do not trust tenant or organization IDs merely because they came from a route, header, query parameter, or client-editable cookie.
- Do not expose first-party bearer or refresh tokens to React.
- Do not allow arbitrary modules to replace authentication, authorization, organization resolution, RLS transaction setup, antiforgery, audit, outbox, or module validation.
- Do not load arbitrary plugin assemblies at runtime in the API process.
- Do not make every service replaceable through a string-keyed DI container.
- Do not introduce general per-user ACL overrides until effective-access explanations, expiry, review, and audit are designed.
- Do not offer generic undo/redo for identity, permission, session, or security-policy changes; use explicit compensating operations only where the domain permits them.
- Do not add organization hierarchy to the v1 kernel unless a real B2B2B requirement justifies the extra recursive authorization and RLS complexity; make it an optional, tested module.
- Do not copy or derive from OpenMercato’s proprietary enterprise security modules.
- Do not copy or derive Agent Orchestrator source, schemas, tests, UI, or wording; use this note, public standards, and independent Trykatch acceptance tests as the implementation input.
- Do not treat model-reported confidence as authorization or allow high-risk mutations to auto-approve.
- Do not run model-authored code in the API process or let a model construct tool source, SQL, shell commands, permission IDs, organization IDs, or approval records.
- Do not persist raw agent callback/session tokens, accept an aggregate “all organizations” mutation context, or permit remote agent metadata to widen local authority.
- Do not emit prompts, outputs, secrets, or high-cardinality tenant/user values into ordinary logs, metrics, or traces.
- Do not let MCP or A2A become a second authentication, authorization, tenant-resolution, audit, or mutation path.
- Do not make an experimental agent framework or a single model provider part of the Trykatch security kernel.
- Do not treat a marketing statement, an in-progress specification, or a large test count as independent evidence of production readiness.

## Bottom line

OpenMercato validates Trykatch’s current direction. Its best reusable lesson is **not a particular login implementation**; it is that modules should contribute through named, validated contracts while a small security kernel owns identity, scope, authorization, persistence boundaries, and auditability.

Trykatch is already stronger in tenant isolation and first-party browser authentication. The highest-value borrowing is therefore session visibility/revocation, optional field encryption, richer build-time extension contracts, command/event/worker composition, and the AgentOps properties of propose-only execution, explicit service identity, durable approvals, least-privilege tools, bounded execution, and measurable evaluations.

The recommended future-proof path is **not** to make AI part of the kernel. It is to make `Trykatch.AgentOps` an optional, replaceable module that is powerful only through the same small, non-replaceable security kernel as every human and module. That preserves modularity while ensuring identity, organization scope, RLS, permissions, commands, audit, and outbox remain the final authority. All implementation must be independently designed in .NET and React under Trykatch's license, using OpenMercato only as architectural evidence.
