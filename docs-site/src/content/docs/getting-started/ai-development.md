---
title: AI-assisted development
description: Use the five bundled project skills to specify, build, extend, review, and verify a Trykatch feature.
---

Generated applications include coding-agent instructions and five skills under `.agents/skills/`. The root `AGENTS.md` routes tasks to those skills and focused backend, React, security, specification, and verification guides. The skills use the existing module CLI and business blueprints, then guide custom implementation and checks.

## Developer onboarding after generation

The template prints a **Start here** message pointing to `docs/developer-onboarding.md` (French: `docs/developer-onboarding.fr.md`). The generated README and `AGENTS.md` link the same guide. Use it with your existing coding assistant, or follow it manually; no extra onboarding provider key or chat service is required. Your coding tool retains its own configuration, costs and data policies.

Start by asking your tool to read `AGENTS.md` and the onboarding guide, inspect the actual checkout and enabled modules, and explain the architecture without changing files. The guide supplies copy-paste prompts for module creation, permissions/RLS, frontend/OpenAPI composition, published extensions, IntegrationEvents/outbox, external API integration and verification. It distinguishes documented design from inspected source and supported scaffolds from custom business work.

React output includes frontend wiring instructions; backend-only output explicitly skips them. The generation message does not launch an AI, install dependencies or start services. It uses the template engine's instruction-only post-action, including when executable scripts are declined. Existing applications are not retrofitted by a template update. In-app AI Help remains a separate user-facing product feature, unchanged by this onboarding kit.

For a concrete first-run-to-first-feature journey, open the generated `docs/first-feature.md` (French: `docs/first-feature.fr.md`). It walks through `doctor`, locked dependency `setup`, explicit API readiness using Aspire's actual URL, Equipment module creation and reading the generator's output. Live CRUD, validation, read/manage permissions, cross-organization isolation and English/French checkpoints distinguish a working feature from a successful build. The repository's `scripts/test-first-feature.sh backend|web` qualifies fresh packed applications and real PostgreSQL relation isolation; it deliberately does not claim live browser or first-time-developer usability verification.

## Start with a feature

Open your generated application in your coding agent and ask:

```text
Read .agents/skills/trykatch-build-module/SKILL.md and implement a shipment
reception workflow using blueprints/shipment-reception.json, including its
React surface. Follow the specification through implementation and verification.
```

Your agent may discover project skills automatically. If it does not, the explicit file path provides the same instructions. The bundle needs no global skill installation or provider credentials; your coding agent uses its own normal configuration.

## Choose a skill

| Skill | Outcome |
| --- | --- |
| `trykatch-spec` | A feature specification with business rules, permissions, state transitions, and testable acceptance cases |
| `trykatch-build-module` | A new module created through the local CLI or a blueprint, with remaining custom behavior implemented |
| `trykatch-extend-module` | A change to existing module source or a published extension point |
| `trykatch-review` | Actionable findings against the request, architecture, authorization, and isolation |
| `trykatch-verify` | Relevant build/test/browser evidence with failed or unavailable checks identified |

A substantial implementation starts with a spec in `docs/specs/`; a small fix can proceed directly. If your request already includes implementation and business decisions are clear, the workflow continues through verification. A specification-only or review-only request stays within that scope.

## What the workflow uses

The local source CLI matches the application's installed contracts. It generates supported organization CRUD modules and blueprint workflows, registers them, builds them, and runs its checks. The agent then implements behavior that generation does not cover. See [module authoring](/modules/authoring/) for generator options and limits.

Guides point to actual reference modules and shared contracts. Tests and server enforcement support the architecture and security rules. Instruction files cannot guarantee that a model follows them; verification reports must distinguish implemented behavior from behavior actually tested.

Both React and backend-only applications include the skills. Backend-only applications skip frontend commands. Skill names stay `trykatch-*` while C# namespaces and project paths in the instructions follow the generated application name.

These skills ship with newly generated applications from a template containing this feature. Updating the global CLI or template does not retrofit existing application files. For an older application, compare the bundle against that application's local contracts before copying it; do not overwrite customized instructions.

## Product assistants are separate

AI Help is the user's guide to the finished application: explain a feature in everyday language, offer documented next steps, and continue with follow-up questions in English or French. The **Get started** suggestion introduces the workspace and its enabled features. Directions respect the user's access; the assistant does not make changes for them. For customer-specific workflows such as Kamenta's cooperatives or mining feasibility, supply reviewed product guides rather than expecting the foundation's example documentation to describe an unimplemented business process.

A model response may request several reads. The runtime validates the entire batch before any read, then executes allowlisted calls sequentially with fresh permission checks. A user turn permits at most four reads in total; writes and automatic retries remain excluded.

AI Help now opens as a website-style chat panel from the organization account menu or bottom-right launcher, with `/assistant` retained as a full-page entry. Enter sends; Shift+Enter inserts a newline. Follow-ups use encrypted, identity/access-bound server continuation for up to four completed exchanges and 20 minutes; transcripts remain in React memory, not persistent browser storage. New conversation, refresh/logout or workspace changes clear the chat.

Six approved guides embedded with the application support architecture, Projects/Documents usage, PostgreSQL RLS isolation, module development and provider/chat questions. Bounded lexical retrieval uses enabled module declarations and filters disabled module guides. **Guides consulted** opens readable authenticated source pages. Documentation access never grants record permissions; help describes the documented design, not arbitrary custom source inspection. Guide excerpts and recent chat context go to the selected provider, alongside any authorized retrieved workspace metadata. Operator-edited help docs ship on rebuild, without a production source checkout or an embedding vendor dependency.

The additional `chat-completions` adapter supports operator-configured compatible endpoints, including DeepSeek UAT, without changing the runtime, tools or UI. Set server-only `Assistant__Provider=chat-completions`, `Assistant__Endpoint=https://api.deepseek.com`, an explicit tool-capable `Assistant__Model` (current example: `deepseek-flash`), and inject the UAT key as `Assistant__ApiKey`. Use `Assistant__ReasoningEffort=none` for DeepSeek's bounded UAT rounds; omit this optional control for endpoints that do not support it. Root and `/v1` base URLs are supported; other base paths are rejected. Compose forwards the settings only to the API, disabled by default. Private reasoning is preserved during tool rounds, not displayed. Deterministic protocol tests are not a live DeepSeek UAT pass. Native vendor protocols still require adapters. See [DeepSeek's reference](https://api-docs.deepseek.com/api/create-chat-completion/).

The generated assistant contract describes explicitly allowlisted API tools, not an executor. The template additionally includes an optional read-only workspace assistant with a provider-neutral `Microsoft.Extensions.AI.IChatClient` runtime and a React page at `/assistant`. Built-in adapters support OpenAI Responses and native Ollama chat; this is not Microsoft Agent Framework. Enable it explicitly with server-only `Assistant__Enabled=true`, `Assistant__Provider` and `Assistant__Model`. OpenAI requires `Assistant__ApiKey`; Ollama requires an explicit root `Assistant__Endpoint` (for example `http://localhost:11434`) and no cloud key for local inference. Non-loopback endpoints require HTTPS. There is no default provider or automatic fallback. Choose a tool-capable model. Questions and authorized retrieved metadata go to the selected endpoint; review its data policies before sensitive workloads. Never put a key in frontend configuration. Other providers require a separately registered/tested adapter, not changes to module tools or the UI.

Runtime tools require an enabled module, an explicitly registered read adapter and caller permission. Requests use organization-scoped transactions/RLS, antiforgery and bounded execution. Document reads expose metadata only. Writes, a human-approval executor, MCP hosting and autonomous agents are not included in v1. The coding skills remain independent of this optional runtime.

Use `module facts [module-id]` to print validated installed ownership, permissions, extension declarations, assistant metadata and source/artifact entrypoints as JSON. It is read-only and available in backend-only applications too. Deterministic runtime/HTTP/PostgreSQL tests verify the assistant's security behavior, not live model quality.
