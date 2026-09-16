---
title: AI-assisted development
description: Use the five bundled project skills to specify, build, extend, review, and verify a Trykatch feature.
---

Generated applications include coding-agent instructions and five skills under `.agents/skills/`. The root `AGENTS.md` routes tasks to those skills and focused backend, React, security, specification, and verification guides. The skills use the existing module CLI and business blueprints, then guide custom implementation and checks.

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

The generated assistant contract describes explicitly allowlisted API tools. It does not include model execution, chat UI, an MCP server, or a running human-approval flow. The development skills do not add those runtime features.
