---
name: trykatch-spec
description: Define a Trykatch feature specification with business rules, ownership, permissions, workflow transitions, and testable acceptance criteria. Use for feature planning or before substantial module implementation; small fixes do not need a new spec.
---

# Specify a feature

Read the application's [AGENTS.md](../../../AGENTS.md) and [specification guide](../../../docs/development/specifications.md). Resolve the application root before running commands; examples there are relative to that root.

Inspect existing `docs/specs/`, relevant architectural decisions, the module catalog, and the closest existing module before choosing new concepts. Reuse an existing specification when it covers the request.

Capture the user's outcome, actors, organization/platform ownership, and business vocabulary. Separate supplied requirements from assumptions. Ask only about missing decisions that materially change behavior or scope; do not invent approval roles, retention policies, or financial rules.

Choose an existing module change, a new CRUD module, a blueprint workflow, or custom application logic using the [backend guide](../../../docs/development/backend.md). Check actual generator capabilities before committing the design to a blueprint. For example, overlapping rental reservations need custom transactional behavior; an editable status enum does not enforce availability.

Write `docs/specs/<feature>.md` with the guide's sections. Link implementation paths that already exist, and label proposed paths as proposed. Use acceptance examples that a reviewer can falsify, including authorization, cross-organization access, invalid transitions, and conflicting writes when applicable. Name the test layer for each example.

If a business decision remains unresolved, mark the affected acceptance criteria blocked and say what decision is needed. Continue independent sections. A specification-only request ends with the spec and open decisions. If the user already requested implementation and the necessary decisions are known, proceed with the build or extension skill; do not create an extra approval gate.
