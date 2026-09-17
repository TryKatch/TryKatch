---
name: trykatch-verify
description: Verify Trykatch changes with the appropriate build, architecture, module, API, PostgreSQL isolation, generated-contract, and frontend checks. Use after implementation or to diagnose a failing verification command and report readiness.
---

# Verify a change

Read [AGENTS.md](../../../AGENTS.md) and the [verification matrix](../../../docs/development/verification.md). Determine the application root, namespace, changed modules, acceptance criteria, and whether `web/package.json` exists. Use actual project paths rather than assuming the original template namespace.

Select checks from the matrix according to the changed behavior. Reuse successful CLI/build/test evidence from this task until a subsequent change invalidates it. Run dependency restoration before commands that require missing dependencies. Keep checks that write shared build outputs sequential.

Use real PostgreSQL tests for organization isolation and transaction claims. Inspect test attributes and output: skipped container tests are not a pass for those claims. If Docker or another prerequisite is unavailable, report the exact blocked check and complete independent checks without weakening or replacing its assertions.

For UI changes, exercise the changed flow in an available browser after starting a local test application with the documented prerequisites. Check the relevant happy path, validation/permission failures, and responsive behavior. Never infer a browser pass from TypeScript compilation. If no browser/session is available, report UI verification as not run.

For frontend verification, consult [Storybook](../../../docs/storybook.md), check coverage of changed visual components and form states, and run affected catalogue interaction/accessibility tests with the backend stopped. Inspect light/dark, English/French and relevant phone/tablet/desktop widths. Keep the real application checks above: mocked stories are not backend or end-to-end evidence.

When a command fails, capture its exit status and diagnostic, identify whether the failure comes from the change or the environment, then rerun the affected check after an authorized fix. A verification-only request does not authorize feature changes or arbitrary dependency upgrades. Do not disable tests, change business requirements, or reset unrelated work to obtain a green result.

Inspect the final diff for generated drift, unrequested changes, secrets, and temporary artifacts. Report commands/results, accepted behavior, and remaining blockers. Mark each acceptance criterion verified, implemented-but-unverified, or incomplete, with the evidence appropriate to its test layer. Update a task-owned spec's verification record when implementation is in scope; a read-only review can report this in the response.
