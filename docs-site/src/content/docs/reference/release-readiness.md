---
title: Release readiness
description: Understand what Trykatch provides and what each generated product must still prove.
---

Trykatch provides a production-oriented architecture and automated qualification gates. It does not certify every generated application or hosting environment.

## Published preview evidence

The 2026-09-17 reconciliation covers Trykatch **0.1.0-preview.25**: release-commit CI passed 282 source unit tests and 236 PostgreSQL integration tests with zero skips. Eight foundation hardening fixes are shipped with regression evidence; the complete stable-release gates remain open. The [evidence ledger](https://github.com/TryKatch/TryKatch/blob/develop/docs/enterprise-foundation-evidence.md) links the immutable release commit, test runs, original fixes and outstanding qualification. An old unchecked box is not proof of a current bug, and passing CI is not production certification.

## Template release gates

The coordinated target **0.1.0-preview.32** removes the Trykatch marketing landing page from newly generated React applications while retaining it on the Trykatch website. It retains the preview.31 organization settings, shared floating controls, developer onboarding and Storybook. Regression checks exercise the generated root route, packaging exclusions and source-site isolation. Availability still requires successful exact-commit main CI, sealed tag qualification and publication.

[Preview.29](https://github.com/TryKatch/TryKatch/releases/tag/v0.1.0-preview.29) is published with both CLI/template NuGet packages and qualified assets. Preview.30 targets its subsequently reported host-form label regression. Neither the Storybook foundation nor this focused correction completes the full catalogue roadmap or provides enterprise certification.

Published **0.1.0-preview.26** added safe exception/abort completion logging, generated SDK/action/first-restore controls and pack-once, digest-bound publication. Its [exact release-commit CI](https://github.com/TryKatch/TryKatch/actions/runs/35246520501) passed all 18 jobs, 282 source unit and 247 PostgreSQL integration tests with zero skips; [tag qualification and publication](https://github.com/TryKatch/TryKatch/actions/runs/35247873752) also passed. That baseline did not include AI Help. Neither release closes all stable-readiness criteria.

- clean builds and test suites with zero compiler warnings;
- cross-organization RLS and authentication integration tests;
- generated OpenAPI and TypeScript client drift checks;
- default, backend-only, optional-module, and difficult-name template matrices;
- container builds, dependency auditing, license allowlisting, SBOM generation, and secret scanning;
- observability configuration, telemetry contracts and collector durable-queue runtime verification.

Default React output receives production-container/browser qualification. Backend-only and optional-module matrices also receive generation/build checks and selected runtime tests; those checks do not prove every optional permutation's complete runtime acceptance. End-to-end log/trace/metric ingestion and delivered-alert evidence, recovery/load drills, manual accessibility, cross-platform/IDE qualification and independent final sign-off remain required before stable promotion.

## Product responsibilities

Every generated product must still validate its own threat model, data classification, capacity, backup and point-in-time recovery, restore drills, key rotation, ingress rules, incident response, and regulatory requirements.

The repository tracks incomplete stable-release gates in the [production-readiness plan](https://github.com/TryKatch/TryKatch/blob/develop/docs/production-readiness-plan.md).
