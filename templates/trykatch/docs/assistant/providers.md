# AI providers and chat behavior

## Provider-neutral inference

The running assistant uses Microsoft.Extensions.AI.IChatClient, not Microsoft Agent Framework. The host explicitly selects an adapter: OpenAI Responses, native Ollama chat or configurable Chat Completions, including a DeepSeek-compatible endpoint. Module tools and guide retrieval use the neutral runtime, not vendor message types. Compatibility still depends on the configured vendor/model protocol; this does not promise ready-made support for every vendor or Azure-specific authentication.

Provider, model and endpoint are operator-owned configuration. Enabled configuration is validated; invalid or failed providers never silently fall back to another vendor. Keys belong on the server. For local Development, use dotnet user-secrets on the API project; user-secrets is development storage, not encrypted production secret management. Never paste keys into chat, commit them to a repository, log them or return them to the browser.

## Read-only chat and memory

AI Help can explain these curated guides and read authorized project/document metadata through explicitly registered module adapters. It cannot mutate records, upload files, run shell commands, inspect arbitrary source code or grant permissions. The model sees guide excerpts as reference data, not instructions. Architecture guidance describes the shipped documented design and enabled declarations, not automatic verification of every custom implementation.

Recent completed text exchanges provide follow-up context: at most four exchanges and an absolute 20-minute conversation lifetime, with additional byte limits. The browser keeps the transcript in memory; close/reopen preserves it while the organization shell is mounted. Refresh, logout, workspace change or New conversation clears it. Older displayed messages may no longer be in the model's context. Expired/changed-access continuation requires a new conversation, without automatic paid retries.

The API allows ten asks per actor per minute and at most eight concurrent assistant asks per instance, without queuing. The concurrency guard runs before organization transaction middleware to bound slow calls holding database connections. Busy requests return 429; other routes do not consume assistant concurrency slots. These limits are per replica, not a distributed quota.

## Privacy and limitations

Questions, recent conversation context, approved guide excerpts and retrieved authorized workspace data are sent to the selected AI provider. Files are not uploaded by this assistant. Provider-specific retention terms still apply; an application-level store flag is not a universal zero-retention guarantee. Answers can be wrong. Inspect Guides consulted and validate important details in the normal UI/API. Internal list pagination metadata should be described in plain language by default, not exposed as JSON or inferred totals.
