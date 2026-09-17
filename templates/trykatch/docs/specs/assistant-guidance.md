# Grounded AI Help and developer guidance

## Outcome and scope

Complete the requested architecture/module-learning experience in AI Help: a member can ask how the application is structured, how Projects/Documents work, how organization isolation works, or where a developer should extend a module; then ask follow-ups and inspect the approved source guides.

This is part of the full AI goal, not part of the earlier chat-only scope. It complements installed `module facts` and project-local coding skills. It does not copy Open Mercato's code or grant the model repository, shell, network, write, administrative, or file-content access. Persistent conversations and autonomous mutation remain separate product/security decisions.

## Evidence and decisions

User explicitly requested prolonged conversations about architecture and module usage, provider independence and enterprise structure. Existing provider-neutral `IChatClient`, bounded chat continuation, explicit module read adapters and organization middleware are retained. Microsoft's [IChatClient guidance](https://learn.microsoft.com/en-us/dotnet/ai/ichatclient) supports provider-independent message/options and DI composition; we intentionally keep invocation authorization in the host rather than installing automatic function invocation.

Assumption: the shipped help guides are non-sensitive, application-approved documentation available to any authenticated active organization member. Organization record reads still require each module's permission. Static help is not proof of an actor's access or a module being enabled. Operators customize the curated docs before building their application.

## Ownership and business rules

The common assistant package owns an immutable, explicitly embedded guide catalog; the host exposes authenticated organization-scoped guide endpoints. Deployment does not require the source checkout. No arbitrary path/URL, source-code scanning, embedding service, new database, or vendor-specific retrieval dependency. Unknown guide IDs return 404. Missing/malformed approved embedded guides fail composition rather than silently changing the allowlist.

Retrieval is deterministic lexical section matching with bounded excerpts, favoring the current question and consulting the latest completed question for follow-ups when needed. Module-specific guides are filtered by the actual enabled module catalog. Runtime supplies approved excerpts as reference data, never as additional instructions; authoritative enabled-module declarations accompany relevant guidance, without caller-editable identity or claims about uninstalled seams. Source links originate from the server's catalog, not model-authored URLs. They mean guides supplied as context, not a guarantee every generated claim is supported.

Knowledge-only help works without project/document read permission. An unavailable record tool is never activated by documentation access. Continuation scope includes knowledge/catalog revision so old answers do not silently survive a documentation/module change. The existing four-exchange/20-minute, request/response, tool, cancellation and no-retry limits remain.

Before organization transaction middleware opens database scope, assistant asks are limited to eight concurrent requests per API instance, with no queue. Excess calls return 429. This supplements the ten asks per actor per minute and existing global fixed-window limit; it is not a distributed quota or a configurable guarantee about every deployment's connection pool. Other endpoint families do not consume assistant concurrency slots.

## Implementation

Implemented: `AssistantKnowledge.cs`, explicitly embedded `docs/assistant/*.md`, immutable guide/source/section records, optional knowledge context on `AssistantRuntime`, host guide list/detail endpoints, additive `HelpAvailable` status and `Guides` answer fields, safe source links and an authenticated guide view with English/French affordances. Legacy ask/status behavior is preserved for runtime consumers without a registered guide catalog. OpenAPI/TanStack client was regenerated after building.

## Acceptance cases and gates

- Unit: every shipped guide is valid and bounded; matching architecture, RLS, Projects/Documents, module authoring and provider questions returns relevant excerpts; follow-ups resolve recent subject; unknown topics do not invent sources. Disabled module guides are not returned. No runtime filesystem/network read occurs.
- Runtime/provider conformance: documentation-only requests work with no authorized record tools on all three adapters; excerpts are data; configured neutral client receives them; forged/forbidden calls remain rejected. Return only server-issued bounded guide sources, never model links or private reasoning.
- HTTP/real PostgreSQL: existing auth/workspace/antiforgery/RLS/rate-limit assertions stay passing; authenticated guide retrieval works, unknown/disabled IDs fail, and a member without record-read permission can get guidance but cannot execute a record read. Tampered/foreign continuation still fails before inference.
- Concurrency: eight held model calls succeed, a caller below its actor quota receives 429 while the slots are occupied, health remains available, and draining requests restores the normal authentication response.
- React/browser: architecture starter, chat guidance/source links, readable guide route, no-permission knowledge-only state, follow-ups and reset/error/cancel behavior, English/French and responsive checks at 320/768/1440px.
- Final delivery audit: inspect original AI artifacts and current code; run full applicable build/unit/architecture/HTTP/frontend/generated/packed-template gates. Distinguish deterministic orchestration/grounding from live DeepSeek model quality and deployed/multi-replica readiness. Keep the full goal active while any required evidence is missing.

## Verification record

### Local checkpoint — 2026-09-17

- Verified: solution build with zero warnings/errors; 399 host unit tests, including 120 assistant cases, pass. Retrieval regression verifies the latest explicit subject wins after a topic change. All three provider adapters pass deterministic knowledge-only conformance with no record grants.
- Verified: the production assistant HTTP scenario passes on isolated real PostgreSQL with no skips, including auth/workspace/antiforgery, permission/RLS boundaries, encrypted continuation, help-only membership, guide allowlisting, forbidden calls and the eight-request concurrency guard/release. The final-state full integration rerun passed all 237 tests with zero failures and zero skips; `tests/Trykatch.IntegrationTests/TestResults/integration-final.trx` records this local run.
- Verified: module doctor, 12 host architecture tests and the prior affected module architecture checks pass. Generated-contract consistency, web workspace typecheck/test/build pass; the host React suite has 58 passing tests. The UI package has no test files; its successful command is not behavior coverage.
- Verified: browser mocks exercise architecture starter, send/clear, a token-bearing follow-up, server guide links opening a separate tab, preserved transcript in the shared floating panel and readable guide view. Chat/panel and guide layouts have no document horizontal overflow at 320/768/1440px. Mobile guide screenshot was visually inspected; French guide labels were checked. Guide bodies are curated English, not a claim of fully translated documents or live answer quality.
- Verified: packed renamed full-stack and backend-only applications pass instruction/link checks and twelve embedded-guide retrieval tests each. Both generated API projects build with zero warnings/errors after dependency restoration. Documentation site builds with zero diagnostics. CI classification tests ensure embedded-guide changes select backend and packaging verification.
- Verified: the rebuilt Development app returns frontend/readiness HTTP 200. An authenticated real local API check confirms enabled help, six readable guides and three authorized read tools; it invokes no provider inference.
- Review: approved embedded corpus/size bounds, enabled-module filtering, knowledge-revision continuation binding, deny-by-default read adapters, source-link safety, generated drift and limiter placement before organization transactions were inspected against this specification/security guide. `git diff --check` passes. No migration, write tool, autonomous invocation, paid retry, arbitrary source access, browser key or persistent chat store was added.

### Authorized DeepSeek UAT and diagnostic follow-up — 2026-09-17

The operator approved three read-only prompts. Architecture guidance succeeded with approved sources and a continuation token, but its genuine token-bearing follow-up failed HTTP 502 `invalid_model_response`. A project-list prompt succeeded, matched the normal API's zero visible active projects, and exposed no raw pagination fields. Before/after project listings matched; no business writes or automatic retries occurred. The three-prompt allowance is exhausted.

The failed provider response's finish reason was not captured. Token exhaustion is only a hypothesis for that original failure. Offline fixtures separately demonstrated that output cutoffs were incorrectly collapsed into generic response errors. All three protocol adapters and the standard client now distinguish `response_limit`; incomplete text is discarded, no continuation is minted and there is no automatic retry. Shared instructions request plain text under 250 words to suit the chat and existing token bound. Existing provider-neutral abstractions, output-token/time limits and security rules are unchanged.

Post-change checks pass: solution build with zero warnings/errors, 401 host unit tests (122 assistant cases), 59 host React tests, real-PostgreSQL assistant HTTP scenario including response-limit classification/no partial answer/token/no retry, web typecheck/build and generated-contract consistency. The broad full integration, architecture and renamed-package runs recorded above occurred before this diagnostic change.

Real-browser checks with all API calls intercepted by mocks verify the specific response-limit notice and preserved question in English/French, exactly one ask per explicit send, and no document horizontal overflow at 320/768/1440px. The French desktop screenshot was visually inspected. These checks make no additional provider calls and do not establish live recovery.

Still unverified: live follow-up success after the change, compliance with the new concise-answer instructions, and nonempty live pagination cases. Renewed operator approval is required for another hosted-model run. This checkpoint does not certify production deployment, distributed quotas, all vendor compatibility or a new-chat accessibility audit; nothing is pushed, merged or published. The broad AI goal is not complete.
