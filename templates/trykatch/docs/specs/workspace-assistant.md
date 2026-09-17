# Read-only workspace assistant

The follow-up chat implementation and its current verification record are documented in [AI Help chat](ai-help-chat.md). Earlier validation checkpoints below describe the initial read-only runtime; they are not a claim of live-provider chat quality.

## Outcome and scope

Complete the safe v1 product-assistant track before durable jobs: an optional server-side model adapter, authenticated organization-scoped endpoint, bounded module-owned read tools, and a translated web interface. Existing coding skills remain available without provider configuration.

Exclusions: model-directed writes, approval executor, autonomous agents, file-content ingestion, MCP hosting, persistent chat history and shared multi-replica quotas. The existing document-update confirmation metadata does not enable execution. These require separate security/product decisions and are not claimed complete.

## Evidence and decisions

User requested completion of AI work before jobs. Assumption: follow the previously proposed read-only-first runtime. Explicit server-side operator opt-in and model selection are required; no provider call occurs by default. OpenAI Responses function protocol: https://developers.openai.com/api/docs/guides/function-calling . `store:false` disables response application-state storage; it is not a guarantee of zero provider retention.

Follow-up requirement: AI-provider independence, including DeepSeek UAT. The host selects an implementation of `Microsoft.Extensions.AI.IChatClient`, not a vendor-specific message interface. Three protocol adapters ship: OpenAI Responses, Ollama `/api/chat`, and configurable `chat-completions` (including DeepSeek-compatible endpoints). This is an inference abstraction, not Microsoft Agent Framework. See [ADR 0013](../adr/0013-provider-neutral-assistant.md). Native Anthropic, Gemini and Azure-specific authentication are future integrations, not automatically supported.

Provider and tool-capable model are explicit server configuration. OpenAI requires a key and a fixed destination; Ollama requires an operator-owned root endpoint and no OpenAI key for local inference. Chat Completions requires a key and operator-owned root or `/v1` base URL. Optional `Assistant__ReasoningEffort` is validated and only emitted for this protocol; DeepSeek UAT uses `none` to avoid spending the bounded 1,024-token budget on thinking. Remote endpoints require HTTPS; HTTP is loopback-only. URL credentials/query/fragments/other base paths and unknown provider IDs are rejected. Disabled or failed providers never fall back to another vendor. No browser/prompt/tool argument chooses a destination. The UI, module tools, permissions and HTTP DTOs are unchanged by provider selection.

Chat Completions acceptance: the same runtime must execute authorized reads and reject forbidden/parallel calls, identity injection and lexical duplicate arguments; reject malformed/incomplete/non-assistant responses; bound response and private reasoning continuation; sanitize HTTP errors without retry; use configured endpoint/model/key and preserve private `reasoning_content` across tool rounds without exposing it as answer text. Protocol compatibility is not a guarantee of arbitrary endpoint/model support or vendor retention policy.

## Ownership and rules

The host owns orchestration/provider configuration; each enabled module explicitly registers its read adapter through `IReadOnlyAssistantTool`. Module descriptors remain the deny-by-default allowlist. Catalog metadata without an adapter, disabled modules, and all non-read-only declarations are unavailable.

Identity and organization derive from the authenticated request, never the prompt or arguments. Permission is checked before tool advertisement and again at execution; adapters repeat it. Queries use the existing organization data interface, EF filters and forced PostgreSQL RLS. No new data relations or migrations.

Requests have no editable user-supplied history/tool results. The [AI Help chat follow-up](ai-help-chat.md) adds scope-bound encrypted continuation for up to four completed text exchanges and an absolute 20-minute lifetime; transcripts/tokens stay in React memory only. Hard per-turn bounds remain: 2,000-character prompt, four serial reads, 45-second deadline, 32 KiB/tool result, 128 KiB continuation context, 256 KiB provider response and 1,024 output tokens/provider round. Module list adapters paginate SQL with 20 items plus lookahead. Documents return metadata only. No persistent chat storage and no automatic paid POST retries.

The built-in adapters also cap the full 128 KiB wire request, including provider-private continuation/reasoning state. Declaration-only tools cannot invoke handlers. Standard clients can return structured function arguments; native adapters additionally retain lexical JSON so duplicate properties fail validation. Non-assistant message roles, forged result content, incomplete finish reasons, stored conversation IDs/background continuations and parallel calls are rejected before execution.

## Permissions and UI

Any authenticated organization membership can open the assistant; actual access is the intersection of registered tools and `projects.read`/`documents.read`. No elevated principal or default permission grant. Cookie POSTs require antiforgery. Assistant requests also have a 10/minute per-actor in-process limiter. Existing global limits apply. The UI displays opt-in/data disclosure, read-only status, loading/empty/error states, cancellation and tool provenance; answers render as plain text, not HTML or model-authored links.

## Acceptance and verification record

- Runtime tests: unknown/mutating/unregistered tools, duplicate calls, forged identity/extra/duplicate arguments, denied/revoked permission, invalid types/ranges, bounds, cancellation and provider protocol failures.
- HTTP/PostgreSQL tests: anonymous rejection, missing workspace, antiforgery, other-organization ID default deny, bounded query results, rate-limit rejection and no domain changes.
- Web tests/browser: disabled configuration, authorized reply/provenance, error/input preservation, cancellation, scope-change/unmount cleanup and responsive layout.
- Developer facts: read-only CLI output from the validated installed catalog, ownership/permission/extension/assistant metadata and installed source paths; no invented uninstalled seams.
- Local checks passed: 344 host unit tests (63 runtime/provider cases), the real-PostgreSQL assistant HTTP scenario, 12 host architecture tests and five architecture tests each for Projects, Documents and Federation. The web suite passes 47 tests, including seven assistant component tests; type checks, production build, generated-contract consistency and module doctor/facts checks pass. Protocol/security tests exercise both real native adapters through fake HTTP responses, not live models.
- Packed-template checks passed for renamed React and backend-only applications; the generated backend-only API builds with zero warnings/errors and the final generated React application's 63 assistant runtime/provider tests pass.
- UI follow-up: padded composer, permission-aware starter prompts that populate but never submit, bounded character counter, separate plain-text answer/provenance card, visible provider disclosure and responsive privacy guidance. Chromium layout checks at 320, 768 and 1,440 pixels use mocked HTTP responses; they are not a live-provider or full browser-to-database test.
- Live provider/model quality remains unverified until operator credentials/model selection and paid-call authorization are available. Model-directed writes and autonomous agents remain explicitly out of scope.

### Configurable Chat Completions follow-up

- Verified: solution build with zero warnings/errors; 369 host unit tests including 88 assistant cases; production HTTP assistant scenario against real PostgreSQL (one passed, none skipped); 12 host architecture tests.
- Verified: root and `/v1` endpoint selection, explicit model/key, optional reasoning effort, private reasoning round-trip and bounds, safe HTTP errors, raw duplicate/identity arguments, parallel-call rejection. Tests use fake HTTP, not provider credentials.
- Verified: both Compose YAML files parse and assistant settings appear only under the API; opt-in remains disabled by default. API DTOs, UI and module data paths are unchanged.
- Live DeepSeek UAT, deployed secret injection and account/model availability remain unverified. Existing generated UAT applications require the source/configuration update; template changes do not retrofit them.
- Verified: packed React/backend-only generation and skill-link checks; renamed backend-only application's 88 assistant tests. API-local user-secrets initialization/configuration is documented; no real secret was set during verification.

### AI Help navigation and composer follow-up

- The account menu exposes **AI Help** below Appearance; its page remains at `/assistant`. The duplicate workspace-sidebar item is removed, and the keyboard jump menu retains access. The page title and new keyboard hint have English/French translations.
- Sending through the button or Enter captures the question and immediately clears the composer. Shift+Enter inserts a newline; IME composition and repeated Enter do not submit. Blank/unavailable submissions and duplicate pending requests are rejected. Failure or cancellation restores the captured question, while organization changes still clear it and discard late responses.
- Verified: generated-contract consistency, workspace typecheck and tests (51 host-web tests, including 11 assistant cases), and production build. Backend contracts/runtime are unchanged; existing runtime and PostgreSQL verification remains applicable.
- Chromium with mocked HTTP responses verifies account-menu placement, Enter submission/empty composer, Shift+Enter newline, and provider-error question recovery. These UI checks do not make paid AI calls or establish live-provider quality.
- Responsive checks at 320, 768 and 1,440 pixels have no document-level horizontal overflow; selecting AI Help on mobile dismisses both the account menu and navigation drawer.
