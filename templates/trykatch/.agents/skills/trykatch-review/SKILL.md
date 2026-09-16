---
name: trykatch-review
description: Review a Trykatch change against its originating request, specification, module architecture, authorization, and organization isolation. Use when reviewing changes, a branch, or a proposed implementation; report findings without implementing fixes unless requested.
---

# Review a change

Read [AGENTS.md](../../../AGENTS.md) and the [verification matrix](../../../docs/development/verification.md). Resolve the requested comparison point and include relevant uncommitted changes. If none was supplied, use the task diff when identifiable; otherwise state the comparison you can support or ask for the missing scope.

Read the originating request/specification and the actual changed call paths. Missing requirements are an uncertainty, not permission to invent intended behavior. Review two dimensions:

- **Behavior:** acceptance criteria, rejected inputs/transitions, concurrent edits, failure recovery, and real integration into the application. Flag a generated shell presented as a completed business feature.
- **Architecture:** public contract compatibility, module project boundaries, explicit registration, migration history, and generated artifact consistency. Use [backend](../../../docs/development/backend.md) and [frontend](../../../docs/development/frontend.md) guidance only for affected surfaces.

For permissions or data changes, follow the [security guide](../../../docs/development/security.md). Trace endpoint and use-case authorization, organization context, EF ownership, forced RLS, and transaction/audit/outbox behavior. A UI permission check or descriptor flag is not evidence of server enforcement.

Read meaningful tests. Run focused non-mutating checks when they resolve uncertainty; do not run the entire suite automatically. Generated-file checks may create build output, but do not fix source, stage, commit, push, or change PR state for a review-only request.

Report actionable defects with file/line, a concrete trigger, resulting behavior, severity, and the violated requirement. Separate requirement gaps from architecture/security defects when both exist; omit empty categories. Distinguish demonstrated defects from questions. If there are no findings, say so and state material verification gaps. Do not claim a code review proves deployment readiness or tenant isolation without the corresponding evidence.
