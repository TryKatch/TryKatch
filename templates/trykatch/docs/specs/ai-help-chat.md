# AI Help chat

## Outcome and scope

Replace the oversized question/answer form with a website-style help chat opened from the organization account menu and a compact help launcher. Include a transcript, typing state, Enter/Shift+Enter, cancellation, failure recovery, New conversation, and recent follow-up context. `/assistant` remains a full-page chat entry point.

Architecture/module teaching was excluded from this chat-only milestone and is now covered by [grounded guidance](assistant-guidance.md). Persisted conversations, streaming, autonomous actions and writes remain excluded. The UI must not imply arbitrary repository knowledge.

## Evidence and decisions

User requested a website-style help chat after asking about follow-up conversations. Assumptions: keep conversations in React memory only; closing the panel preserves the current conversation while the organization shell remains mounted, but refresh/logout/unmount clears it. Use a server-issued encrypted/authenticated continuation token, not caller-editable chat roles/history. Its technical lifetime is 20 minutes from conversation creation and it retains at most four completed exchanges, bounded to 64 KiB. Display these limits accurately.

## Ownership, permissions and rules

The host owns chat/token handling; existing module adapters own reads. Bind continuation to authenticated actor, organization, membership, current permissions, available tools and provider/model configuration. Reject invalid, expired, tampered or differently scoped tokens before inference. Recheck normal tool permissions every turn. Retain only completed user/assistant text, never tool payloads, function calls or private provider reasoning. Prior answers are context, not a source of current data or authorization.

No database schema or chat persistence. No local/session storage or content logging. Tokens use existing ASP.NET Core Data Protection; replicas need the same configured application/key ring, otherwise continuation fails closed. New conversation discards the browser's token; old tokens remain valid only within their original bound scope/lifetime. No automatic paid retries.

Keep pagination inside tools; instruct all providers to describe limitations in plain language, omit raw metadata and not infer total pages/counts. Starter prompts no longer ask for a first page.

## Implementation

Extend `AssistantRequest` with optional `ConversationToken`, and `AssistantAnswer` with optional continuation token, retaining operation IDs and single-turn compatibility. The controller validates tokens using resolved organization context; the provider-neutral runtime accepts only the validated completed exchanges. The shared chat owns composer/transcript behavior. A shell-level help panel uses the existing accessible Dialog, alongside the direct route.

## Acceptance and verification

- Runtime/token unit tests: follow-up includes only validated text; forgery, expiration, identity/access mismatch and bounds fail closed; token excludes tool/private reasoning and is encrypted.
- Production HTTP/PostgreSQL: legacy single-turn, valid continuation, tampered and foreign-scope continuation, antiforgery, read-only tools and existing rate limits.
- React/browser: multi-turn transcript and captured continuation, send/clear, recovery, reset, close/reopen, permission/scope cleanup, dialog keyboard dismissal and no overflow at 320/768/1440px.

### Verification record — 2026-09-16

Implemented and verified at the appropriate deterministic test layer:

- Solution build passed with no warnings/errors; module doctor and 12 host architecture checks passed.
- 383 host unit tests passed, including 102 assistant runtime/conversation cases. These cover all three provider wire adapters, plaintext-only follow-up history, tamper/identity/access rejection, absolute expiration, exchange/byte bounds and Unicode limits.
- The assistant production HTTP acceptance scenario passed against isolated real PostgreSQL with no skips. It exercises legacy requests, actual issued-token continuation, invalid/foreign-scope rejection before provider invocation, authentication/workspace resolution, antiforgery, permission-scoped reads, foreign-record isolation and rate limits. Inference uses a deterministic fake provider.
- Web workspace typecheck, tests, production build and generated-contract consistency passed. After adding scope/reset regression cases, all 55 host React tests passed, including 15 assistant cases; unchanged package tests had also passed earlier in this task.
- Browser checks with mocked assistant HTTP responses passed for the account-menu panel entry, multi-turn continuation/transcript, Enter submission and composer clearing, Shift+Enter, failure recovery, New conversation, close/reopen retention, Escape/focus restoration, and no-permission disabled input. Panel bounds were checked at 320/768/1440px; mobile and dark-mode screenshots were visually inspected after responsive transitions settled. Only one conversation live region is rendered when the panel is open over `/assistant`.
- The three existing landing/login/recovery browser accessibility tests passed. They are not an accessibility certification of the new chat panel.
- Packed-template generation/instruction-link checks passed for renamed full-stack and backend-only applications. This does not claim those generated applications were fully built/deployed.
- Task-owned code paths were reviewed against this specification and the authorization guide; `git diff --check` passed. No schema changes, write tools, provider fallback, key exposure or chat persistence were introduced.

Implemented but not verified against a live paid provider: conversational answer quality and compliance with the plain-language/no-raw-pagination instructions. No live DeepSeek inference was invoked during these implementation checks. Local API readiness returned HTTP 200.

At this milestone, curated architecture/module documentation retrieval was incomplete; the subsequent [grounded guidance](assistant-guidance.md) implementation supersedes that limitation. Persistent saved chats, streaming and autonomous/write actions remain separate capabilities. The verification figures above are this milestone's checkpoint, not the newer guidance gate results.
