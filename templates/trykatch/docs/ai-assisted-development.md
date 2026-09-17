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

`AssistantToolDescriptor` is the module seam for product-facing AI tools. In React applications, the generation pipeline converts allowlisted OpenAPI operations into strict function-tool schemas in `docs/generated/assistant-contract.json`. These describe API bindings; they are not a runtime executor.

The optional [read-only workspace assistant](specs/workspace-assistant.md) uses the provider-neutral `Microsoft.Extensions.AI.IChatClient` interface, `/api/v1/assistant/status`, `/api/v1/assistant/ask`, and a React page at `/assistant`. Built-in adapters support OpenAI Responses, native Ollama chat and configurable Chat Completions; the runtime does not use provider wire formats. This is an AI abstraction, not Microsoft Agent Framework or an autonomous agent system. It executes only explicitly registered `IReadOnlyAssistantTool` adapters from enabled modules and rechecks their permissions. Projects supports bounded list/get reads; Documents supports bounded metadata lists, not file ingestion. Runtime adapter schemas intentionally narrow the general API operations (active records, 20-item pages, bounded fields); the browser-generated API contract is not loaded as executable configuration.

It is disabled by default and has no default provider/model or fallback to OpenAI. Set `Assistant__Enabled=true`, `Assistant__Provider` and `Assistant__Model` through server environment or user secrets. Select a model with native tool-calling support; adapter support does not imply every model works.

| Provider | Server settings | Protocol |
| --- | --- | --- |
| `openai` | `Assistant__ApiKey` required; leave `Assistant__Endpoint` empty | Fixed OpenAI Responses destination; `store:false` |
| `ollama` | `Assistant__Endpoint=http://localhost:11434` for local inference; key optional | Native `/api/chat`, non-streaming |
| `chat-completions` | Explicit root or `/v1` base `Assistant__Endpoint`, `Assistant__ApiKey` required | Appends `/chat/completions`, non-streaming; optional `Assistant__ReasoningEffort` |

The configurable Chat Completions adapter supports DeepSeek-compatible endpoints through the same `IChatClient` interface. For DeepSeek UAT, inject these settings into the **API process/container**, not the browser. Store the real key in the UAT backend's secret manager under `Assistant__ApiKey` (equivalent .NET key `Assistant:ApiKey`); the placeholder below is not a usable key:

```text
Assistant__Enabled=true
Assistant__Provider=chat-completions
Assistant__Endpoint=https://api.deepseek.com
Assistant__Model=deepseek-flash
Assistant__ReasoningEffort=none
Assistant__ApiKey=<injected UAT secret>
```

The root or `/v1` base URL is operator-owned; other paths, URL credentials, query strings and fragments are rejected. Remote endpoints require HTTPS; HTTP is loopback-only. The shipped Compose files forward these settings to the API only, disabled by default. Inject the key from your secret manager into the deployment process environment; do not commit a `.env` containing it or print resolved Compose configuration with real secrets. For local UAT, supply the settings to the API's process environment.

This model/endpoint example follows the current [DeepSeek Chat Completions reference](https://api-docs.deepseek.com/api/create-chat-completion/); verify the tool-capable model available to your account. Reasoning effort is optional and endpoint-specific: omit it for endpoints that do not support it. DeepSeek defaults to thinking; `none` disables it for the assistant's bounded 1,024-token rounds. Private `reasoning_content` round-trips when returned during tool use, without becoming UI answer text; see [thinking mode](https://api-docs.deepseek.com/guides/thinking_mode/). Protocol conformance tests are deterministic, not a live DeepSeek UAT pass. Updating the template does not retrofit existing generated applications; update their adapter/configuration source before testing. Compatible protocol support does not guarantee every vendor/model works or has the same data policy.

For laptop UAT, .NET user-secrets is supported by the API's default configuration when running in `Development`. From `src/API/Trykatch.Api` (use the renamed API directory in generated applications), initialize the API project and set the same options using colon-separated keys:

```bash
dotnet user-secrets init
dotnet user-secrets set "Assistant:Enabled" "true"
dotnet user-secrets set "Assistant:Provider" "chat-completions"
dotnet user-secrets set "Assistant:Endpoint" "https://api.deepseek.com"
dotnet user-secrets set "Assistant:Model" "deepseek-flash"
dotnet user-secrets set "Assistant:ReasoningEffort" "none"
```

On macOS zsh, prompt for the key without embedding its literal value in shell history:

```zsh
read -s "deepseek_uat_key?DeepSeek UAT API key: "
dotnet user-secrets set "Assistant:ApiKey" "$deepseek_uat_key"
unset deepseek_uat_key
```

Restart the API after setup. Initialize/set secrets on the API project, not the AppHost or React project. User-secrets are outside the repository but **not encrypted** and are intended for local development, not hosted production. An environment named `UAT` does not automatically load them; use `Development` for laptop testing or explicitly arrange another configuration source. Existing environment variables override user-secrets; remove stale `Assistant__...` overrides if changing providers. See [Microsoft's Secret Manager guidance](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets?view=aspnetcore-10.0).

Ollama endpoints are explicitly operator-owned root URLs: HTTP loopback or HTTPS. Credentials in URLs, query strings, fragments and base paths are rejected; redirects are disabled. An optional Ollama key becomes a Bearer header for a secured endpoint. No endpoint can be selected by a question, tool argument or browser field. Local Ollama does not require an OpenAI account or key. Remote/hosted inference may incur provider charges. Never commit keys or place them in Vite/frontend configuration. Operator opt-in authorizes sending questions and retrieved metadata to the selected inference endpoint. `store:false` is OpenAI-specific, not a zero-retention guarantee or a portable provider data policy; review each endpoint's data controls before sensitive workloads.

To add another vendor, register its `IChatClient` at the host's composition seam and its server-side configuration validation. Module tools and the UI need no changes. Supply declaration-only tools, keep tool execution in `AssistantRuntime`, honor cancellation, reject incomplete/background/stateful responses, preserve any private continuation state within the adapter and enforce the existing wire/result bounds. Do not enable automatic function invocation, retries, chat persistence or sensitive-content logging. Native Anthropic, Gemini, Azure and other vendor adapters are not shipped by this change and require their own configuration, protocol and conformance tests. See [ADR 0013](adr/0013-provider-neutral-assistant.md).

References: [Microsoft IChatClient](https://learn.microsoft.com/en-us/dotnet/ai/ichatclient), [OpenAI function calling](https://developers.openai.com/api/docs/guides/function-calling), [native Ollama chat](https://docs.ollama.com/api/chat), [OpenAI data controls](https://developers.openai.com/api/docs/guides/your-data).

AI Help is available from the organization account menu, a bottom-right help button, or `/assistant`. Its chat transcript and continuation token live only in React memory, not local/session storage. Closing and reopening the panel keeps the conversation while the organization shell remains mounted; refresh/logout or New conversation clears it. The server encrypts/authenticates the continuation token and binds it to actor, organization, membership, current permissions/tools and provider/model settings. It retains at most four completed question/answer exchanges, bounded to 64 KiB, for an absolute 20-minute conversation lifetime. Expired, forged or differently scoped context fails closed before inference. Replicas must share the configured Data Protection application/key ring to accept the same tokens. New conversation discards the browser token; it does not revoke an already issued token outside its original scope/lifetime.

The browser never submits editable chat roles, tool results or organization IDs. Only verified completed text is supplied as follow-up context, not private reasoning or raw tool payloads. Prior answers cannot grant permissions or replace current scoped reads. Each turn retains tool-call, payload, output-token and duration limits and uses the existing scoped PostgreSQL transaction. Cookie requests require antiforgery and have a 10/minute per-actor in-process limit, in addition to global limits. At most eight assistant asks run concurrently per API instance, without a queue; excess asks return 429 before organization transaction middleware. Other routes do not consume those slots. This is a per-replica guard, not distributed quota enforcement. No automatic paid POST retries. Answers render as plain text and may be incorrect; collapsed Sources labels identify modules whose adapters actually executed, not a guarantee of factual accuracy. System instructions require natural-language limitations instead of raw pagination metadata; model compliance still needs UAT.

AI Help also supports grounded architecture and module guidance from six explicitly approved guides in `docs/assistant/`, embedded in the common assistant assembly at build time. Documentation-only help is available to active organization members without granting project/document record permissions. Deterministic section retrieval supplies at most four bounded excerpts (16 KiB reference context) and enabled module declarations. Module-specific guides disappear when their modules are disabled. No source checkout, runtime filesystem scan, arbitrary URL fetching or embedding service is required. Operators customize these non-sensitive guides before building; they describe the documented design, not an inspection of custom source modifications.

For end users, AI Help explains a documented feature's purpose and offers short next steps in the user's language, with friendly feature names instead of technical permission identifiers. The getting-started guide covers workspace navigation, Projects, Documents and administrator invitations. Permission-dependent steps are conditional: documentation is not proof the caller can perform an action, and the assistant never performs changes.

When turning this template into a product such as Kamenta, replace or extend the approved documentation with that product's real user tasks, screen names, role boundaries and workflows before building. A mining or cooperative workflow is not implemented or understood merely because it was mentioned in a prompt. To register a new guide, add its explicit approved declaration (including the owning module for module-specific help) in `AssistantKnowledge`, its non-sensitive Markdown under `docs/assistant/`, and its explicit embedded resource in the common assistant project. Preserve guide/section/context bounds and server-owned source links. Add retrieval, disabled-module and permission-boundary tests; regenerate contracts only if the API changed. Do not add runtime source scanning, arbitrary uploads/URLs or privileged record access. Updated guide/catalog revisions invalidate older conversation context.

Providers may return multiple read requests in one response. The runtime validates the whole batch before any read, then executes one read at a time with per-read permission checks. Batches share the existing four-read total turn budget and do not enable SDK invocation, writes, parallel execution or retries.

Collapsed **Guides consulted** links open readable authenticated guide pages in a separate tab, preserving the current chat. Links and revisions come from the server's approved catalog, not model-authored URLs. They identify guides supplied as context, not proof that every answer is correct. `/api/v1/assistant/guides` lists available guides; `/api/v1/assistant/guides/{id}` returns approved sections, with unknown/disabled IDs returning 404. The continuation fingerprint includes the guide/catalog revision. Questions, recent chat context and selected help excerpts are disclosed as provider inputs. See [grounded guidance](specs/assistant-guidance.md) and [chat behavior](specs/ai-help-chat.md).

Developer discovery is available through `module facts [module-id]`: JSON facts from the validated installed catalog, including ownership, permissions, extension IDs, assistant metadata and source/artifact entrypoints. This works in backend-only applications too and does not change files. Facts expose installed declarations, not invented extension points or runtime availability.

The generated contract is not an authorization mechanism and does not contain credentials. Runtime reads go through the normal API session, inferred organization context, permission checks, transaction-local PostgreSQL settings, and RLS. There is no approval executor in v1: mutating and destructive tools are never advertised or executed, regardless of confirmation metadata.

The current document-update descriptor declares confirmation metadata, but that flag does not implement an approval flow. Before enabling a write tool in a future adapter, implement request-body schema/binding support, idempotency, audit events, confirmation UX, rate limiting, evaluation cases, and failure recovery for that operation. Autonomous agents, an MCP server and a generative coding-agent judge are separate future capabilities.

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
