# Workspace assistant v1 — local verification

Latest checkpoint: see [AI delivery audit](plans/ai-delivery-audit.md) and [grounded AI Help verification](../templates/trykatch/docs/specs/assistant-guidance.md). The 2026-09-17 implementation includes shared help chat, encrypted bounded follow-ups, six approved architecture/module guides and per-instance assistant concurrency protection. Three authorized read-only DeepSeek UAT prompts were attempted: architecture and workspace reads succeeded; the token-bearing follow-up failed with HTTP 502 `invalid_model_response`. The original provider finish reason was not captured. Offline cutoff diagnostics and concise/plain-text instructions were subsequently improved: 401 host unit tests (122 assistant cases), 59 host React tests, the real-PostgreSQL assistant HTTP scenario, solution build, typecheck/build and generated-contract checks pass. The prior full integration run passed 237 tests without skips before this change. Live re-verification needs renewed operator approval; the original three-prompt allowance is exhausted. Figures below are historical milestones, not the latest total.

## Authorized live UAT — 2026-09-17

| Prompt | Observed result |
| --- | --- |
| Explain application architecture and five module layers using the approved architecture guide | HTTP 200; guide-grounded modular-monolith explanation, server-issued architecture/document guide sources and continuation token. Formatting was unnecessarily verbose Markdown for the plain-text chat. |
| Explain that further, with examples (real continuation token from the preceding response) | HTTP 502 `invalid_model_response`; no retry. Cause cannot be distinguished from this generic response. |
| Show me the projects in this workspace | HTTP 200; authorized project-list tool used. Both the normal API and answer reported no visible active projects; no raw pagination fields in the answer. Before/after normal listings matched. |

The configured server-side key was used, never copied into browser code or printed. Hosted inference may incur charges. No business records were written; development-account authentication/sign-out occurred. These observations are not proof that every provider, nonempty list, follow-up or enterprise deployment works. The subsequent offline fix distinguishes output cutoffs without accepting incomplete responses or raising token limits. Further paid requests require separate consent.

Date: 2026-09-16. Branch: `feat/workspace-assistant`. Local, uncommitted work; nothing pushed, merged or deployed. Durable jobs have not been started.

## Delivered

Provider-neutral `Microsoft.Extensions.AI.IChatClient` runtime with server-side OpenAI Responses and native Ollama adapters, authenticated organization-scoped status/ask endpoints, module-owned bounded project reads and document metadata reads, deny-by-default tool execution, antiforgery and per-actor request limits. The assistant is disabled until an operator explicitly selects provider/model and configures the chosen endpoint's settings. Local Ollama requires no OpenAI key; OpenAI requires a server-side key. No browser key, uploaded file contents, stored chat history or model-directed writes. This is not Microsoft Agent Framework.

The developer `module facts [module-id]` command emits validated installed-manifest declarations, source paths, permissions, ownership and extension metadata. It does not invent runtime adapters or infer undocumented source symbols.

The UI follow-up fixes the unpadded initial panel and stretched badge: responsive composer, permission-aware starter prompts, character counter, loading/cancel states, separate plain-text answer card, tool provenance, provider disclosure and privacy guidance. New copy is translated into French and uses the existing design/theme tokens.

## Checks

- Locked .NET restore and solution build: pass, zero warnings/errors.
- Host unit suite: 344 passed; 63 are assistant runtime/provider cases. Both real adapters execute the same authorized read contract through fake HTTP, and reject forbidden tools, extra/duplicate arguments, malformed/oversized responses and oversized provider-private continuation state. Standard structured function calls also work without native adapter metadata. Unsafe/unknown configuration never silently selects OpenAI.
- Assistant HTTP scenario: passed against real PostgreSQL and production request middleware, with a deterministic fake model. Covers authentication, workspace selection, CSRF, forbidden organization/body arguments, cross-tenant project reads, bounded pagination, metadata-only documents, mutating-tool rejection and HTTP 429.
- Architecture: 12 host tests and five tests each for Projects, Documents and Federation passed.
- Frontend suite: 47 host web tests passed, including seven assistant tests. Other workspace package/module suites also passed earlier in this implementation.
- Workspace TypeScript checks, production build and generated assistant-contract consistency: pass.
- Module doctor: healthy; module facts output checked for Projects. Facts unit test verifies read-only behavior and rejects unknown modules.
- Packed template: React and backend-only generation/skill-link checks passed. Renamed backend-only API builds without warnings/errors; final renamed React application's 63 runtime/provider tests passed. This is not a full generated application's integration/release certification.
- Chromium: authorized starter populates the question without submitting; explicit submit renders an answer and provenance. No horizontal overflow at 320 and 768 pixels; desktop layout visually checked at 1,440 pixels. Screenshots are under `templates/trykatch/output/playwright/`. Browser HTTP responses are mocked; backend/database correctness is covered separately by the integration scenario.

## Remaining boundary

No live provider call or model-quality evaluation was made. Operator provider/model setup and an authorized live smoke test remain before claiming end-to-end readiness (a paid key/call is needed only for the chosen hosted provider). `store:false` is OpenAI-specific and not a zero-retention guarantee. Provider independence does not claim ready-made Anthropic, Gemini, Azure or arbitrary vendor adapters. Writes, approval execution, autonomous agents, file ingestion, MCP hosting, persistent conversation memory and cross-replica quota storage are excluded from this read-only v1.

Only `Microsoft.Extensions.AI.Abstractions` 10.9.0 is added; no provider SDK/agent-framework dependency. Restoring regenerated affected lockfiles, including Aspire's existing transitive abstraction alignment and stale project-reference metadata. Locked restore passes. Destination selection stays server/operator-owned; HTTPS remote or loopback HTTP, no redirects or automatic retries. HTTP contracts, module ownership/permissions and the UI were preserved.

Existing research/blog drafts and unmerged worktrees are preserved. No published template version was changed.

Disposable packed-template acceptance directories were moved to macOS Trash (`trykatch-assistant-acceptance-gIBMUY` and `trykatch-assistant-acceptance-uhYoWz`); they remain recoverable.

The provider-neutral follow-up's temporary acceptance applications were likewise moved to Trash as `trykatch-assistant-acceptance-1NXDH7` and `trykatch-assistant-acceptance-ttppiE`. No source worktree was removed: secondary branches remain unmerged and are preserved.

## DeepSeek-compatible protocol follow-up

Added a third `IChatClient` adapter selected by `Assistant__Provider=chat-completions`. Its operator-configured root or `/v1` base URL, model and server-only API key allow compatible vendors without changes to runtime, tools, HTTP DTOs or UI. Optional validated reasoning effort is emitted only when configured; DeepSeek UAT documentation uses `none` to preserve the 1,024-token limit for non-thinking output. Private `reasoning_content` is retained across tool rounds, bounded in the wire context and never rendered. No arbitrary request options, automatic invocation, retry or fallback were added.

Current checks: solution build passed with zero warnings/errors; 369 host unit tests passed including 88 assistant cases; PostgreSQL-backed production assistant HTTP test passed (one, zero skipped); 12 architecture tests passed. Both Compose files parse and forward assistant settings only to the API, disabled by default. Diff whitespace checks passed. Protocol tests use deterministic fake HTTP; no paid call or real secret was supplied. Live DeepSeek UAT remains pending, and the existing generated UAT application has not been retrofitted or deployed.

Prior packed-template and UI checks above describe their earlier checkpoints, not a claim that the new adapter has been live-tested. Native vendor-specific protocols/authentication still require adapters; protocol compatibility is not universal model or data-policy compatibility.

Current packed-template follow-up: React and backend-only generation/skill-link checks pass; the namespace-renamed backend-only application's 88 assistant tests pass. Local .NET user-secrets setup is documented with API initialization, colon-separated keys, a masked zsh prompt and the Development-only/unencrypted-storage caveats. The disposable acceptance workspace was moved to macOS Trash as `trykatch-compatible-acceptance-fZMeWQ` after checks completed and remains recoverable.
