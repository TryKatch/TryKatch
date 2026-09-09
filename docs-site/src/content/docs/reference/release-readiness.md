---
title: Release readiness
description: Understand what Trykatch provides and what each generated product must still prove.
---

Trykatch provides a production-oriented architecture and automated qualification gates. It does not certify every generated application or hosting environment.

## Template release gates

- clean builds and test suites with zero compiler warnings;
- cross-organization RLS and authentication integration tests;
- generated OpenAPI and TypeScript client drift checks;
- default, backend-only, optional-module, and difficult-name template matrices;
- container builds, dependency auditing, license allowlisting, SBOM generation, and secret scanning;
- observability ingestion and readiness verification.

## Product responsibilities

Every generated product must still validate its own threat model, data classification, capacity, backup and point-in-time recovery, restore drills, key rotation, ingress rules, incident response, and regulatory requirements.

The repository tracks incomplete stable-release gates in the [production-readiness plan](https://github.com/TryKatch/TryKatch/blob/develop/docs/production-readiness-plan.md).
