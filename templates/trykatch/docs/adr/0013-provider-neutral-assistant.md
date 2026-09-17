# ADR 0013: Provider-neutral assistant inference

Status: Accepted for the local read-only v1 implementation.

## Context

The first runtime accepted OpenAI-shaped JSON messages and registered one fixed provider. The user explicitly requires AI-provider independence. An interface with only one adapter and vendor-specific inputs is insufficient evidence of that independence.

## Decision

`AssistantRuntime` consumes `Microsoft.Extensions.AI.Abstractions`' `IChatClient`, `ChatMessage`, `ChatResponse` and function content types. It advertises non-invocable `AIFunctionDeclaration` metadata, not callable `AIFunction` instances. Trykatch retains orchestration, permission rechecks, raw/schema argument validation, four-read bounds and server-produced tool results. No automatic function-invocation pipeline is installed.

Three built-in adapters translate protocols: OpenAI Responses, Ollama `/api/chat`, and configurable Chat Completions. The latter supports DeepSeek UAT and other compatible endpoints without a per-vendor runtime fork. Provider-private reasoning/continuation state stays in the adapter's message `RawRepresentation`; full serialized wire requests are capped to prevent that private state bypassing context bounds. Adapters retain raw argument JSON as provider-neutral metadata before dictionary conversion, so duplicate keys cannot bypass runtime validation. Standard third-party clients may supply structured function arguments without that metadata.

Configuration explicitly selects a provider and tool-capable model. Disabled configuration creates a non-networking disabled client. Invalid or unknown enabled providers fail configuration validation; failures never fall back to another vendor. OpenAI requires a server key and uses a fixed destination. Ollama requires an operator-configured root URL; local inference requires no OpenAI key. Chat Completions requires a key and root or `/v1` base URL. Optional validated reasoning effort is sent only when explicitly configured for that protocol. Private `reasoning_content` is retained across tool rounds but never rendered. Unsupported finish reasons, multiple choices and contradictory finish/tool-call states fail closed; parallel calls are rejected by the runtime regardless of provider behavior. Destinations require HTTP loopback or HTTPS; credentials/query/fragment/other base paths are rejected. Named HTTP clients disable redirects and inherited retries.

## Consequences

Provider changes do not affect module tools, permissions, organization/RLS scope, HTTP request/response contracts or the UI. Further vendor adapters register at host composition with provider-specific configuration and conformance tests. No claim of automatic support for all vendors/models, Azure-specific authentication, streaming, autonomous agents, approvals or model-directed writes. Microsoft.Extensions.AI is the standard abstraction, not Microsoft Agent Framework.

Only the abstractions package is added, centrally pinned at 10.9.0. NuGet regenerates affected project lockfiles; the central pin also aligns Aspire's existing transitive abstraction. No provider SDK or agent-framework package is introduced.

## Evidence

All three adapters are tested through `IChatClient` and the same runtime: authorized reads, forbidden writes, identity/extra/duplicate arguments, safe errors, payload bounds, opaque continuation bounds and explicit destinations. Deterministic HTTP fixtures do not establish live provider/model quality. PostgreSQL HTTP acceptance separately verifies the unchanged authorization/isolation path. DeepSeek's current [Chat Completions](https://api-docs.deepseek.com/api/create-chat-completion/) and [thinking-mode continuation](https://api-docs.deepseek.com/guides/thinking_mode/) documentation informs the compatible adapter; no live credentials are needed for deterministic tests.

Sources: [IChatClient](https://learn.microsoft.com/en-us/dotnet/ai/ichatclient), [declaration-only tools](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.aifunctionfactory.createdeclaration?view=net-10.0-pp), [OpenAI protocol](https://developers.openai.com/api/docs/guides/function-calling), [native Ollama chat](https://docs.ollama.com/api/chat).
