# Business module blueprint foundation

## Outcome and boundary

Generate an organization-owned domain, API, persistence layer and optional React surface from one reviewed JSON blueprint. The first acceptance domain is shipment reception: Draft → Submitted → Accepted or Rejected, with positive weights, verified-document acceptance and a bounded rejection reason. This is a tested foundation, not a claim of certification by Microsoft, Apple or an external auditor.

Existing `--fields` CRUD generation remains supported. Blueprint v1 deliberately excludes relationships, multi-aggregate transactions, executable expressions, arbitrary assignment, regeneration over customized source, pagination and durable request idempotency. These require separate specifications and PRs; optimistic concurrency is not a substitute for durable idempotency.

## Delivery checklist

- [x] Strict, versioned, size/depth-bounded JSON authoring contract; unknown and duplicate properties rejected.
- [x] Read-only CLI validation and naming/host compatibility preflight.
- [x] Embedded templates and retained normalized blueprint; atomic generation/rollback.
- [x] Domain-owned transitions and constraints; private state; GUID optimistic-concurrency token.
- [x] Separate read handlers, create/update handlers and workflow command handlers; shared recovery commands and read-model mapping.
- [x] Central organization EF filter, PostgreSQL forced RLS, permission metadata and application checks, antiforgery, transactional audit/outbox.
- [x] Typed OpenAPI clients and generated React forms/actions, translated labels, state visibility, conflict recovery preserving input.
- [x] Host binding-error response correction: invalid JSON/unknown fields return safe client errors rather than 500.
- [x] English/French authoring documentation and CLI help.
- [x] CI matrix for packed backend-only/full-stack generation and independent PostgreSQL acceptance.
- [x] Review corrections: shared bilingual navigation, conservative mixed-guard availability, canonical registry formatting and accessible action confirmations.
- [x] Final packed-source matrix and full regression checks on the promoted code.
- [x] Review and merge feature PR into develop; promote validated develop to main.
- [x] Repeat browser UAT from the publicly published packages, including object-storage startup.
- [x] Publish coordinated CLI/template preview and verify the public-package installation path and live documentation.

## Delivery evidence

- Implementation: [PR #96](https://github.com/TryKatch/TryKatch/pull/96); both code-review findings were corrected and resolved.
- Main promotion: [PR #97](https://github.com/TryKatch/TryKatch/pull/97), commit `7381b50c8a2343db29ef4feadfa16fada3617f4c`.
- Full promotion regression: [successful run](https://github.com/TryKatch/TryKatch/actions/runs/34973675831).
- Full main regression: [successful run](https://github.com/TryKatch/TryKatch/actions/runs/34974018471).
- The backend-only and React blueprint jobs generated from packed artifacts and passed the real PostgreSQL acceptance tests. Local verification also passed 231 host unit tests and 12 architecture tests.
- Browser qualification exercised shipment creation, submission and acceptance, locked editing after submission, French labels, dark mode and a 390-pixel mobile viewport. Public-package qualification is tracked separately above.
- Greptile could not perform a new promotion review because the trial credit allowance was exhausted. These results do not claim a new Greptile review or an independent enterprise/security certification.
- Public release: [preview.21](https://github.com/TryKatch/TryKatch/releases/tag/v0.1.0-preview.21), qualified by [release run](https://github.com/TryKatch/TryKatch/actions/runs/34975352202). Both packages installed from NuGet.org into an isolated tool directory and template hive.
- Public-package UAT generated `KametalBlueprintDemo`, created its full-stack ShipmentReceptions module, and passed generator verification. Browser checks covered login, Draft → Submitted → Rejected with a reason, French navigation, document upload and byte-for-byte download, and MinIO startup. The live installation guides and landing page showed preview.21.
- A local PATH mismatch selected pnpm 11.19.0 instead of the project's pinned 10.17.1 during Aspire startup. Selecting the Corepack-compatible Node.js PATH fixed startup without changing application source or deleting dependencies. English and French installation guides document the prerequisite and recovery steps.

## Acceptance evidence required for release

1. Generate from the packed template using the packed CLI, with a non-Trykatch application namespace.
2. Build and run generated domain/architecture tests and, for React, client generation, typecheck, tests and production build.
3. Log in with a real cookie and antiforgery token against a Testcontainers PostgreSQL instance with separate least-privilege runtime roles.
4. Exercise valid and invalid shipment transitions, positive-weight validation, reason validation, overposting rejection and recovery-state preservation.
5. Race two submissions with one version: exactly one success, one conflict and one persisted audit/event pair.
6. Revoke review permission in the database: the next request must hide the action and reject direct execution with 403.
7. Inspect migrated RLS and prove cross-organization reads/writes fail through both EF and PostgreSQL runtime roles for every declared organization relation.
8. Prove generation failures restore original files and remove the new module.

## Host upgrade boundary

The coordinated delivery version is `0.1.0-preview.21`. The release job qualifies both packed blueprint modes before publishing either package. Installation commands, landing page and English/French installation guides move together with `RELEASE_VERSION`.

The catalog capability `business-blueprints-v1` declares support for safe HTTP request-binding errors, version-aware Archive integration and bilingual navigation labels. A CLI update does not update an existing generated application's source. The generator refuses an older host without this capability. Do not set the marker until these host changes and their tests have been applied.

## Follow-up PRs

1. Bounded server-side pagination, filtering and sorting with a documented maximum page size and React table integration.
2. Reviewed relationships/value objects, cross-field invariants on save, and explicit domain-specific command inputs beyond bounded text.
3. Durable idempotency for retried external commands, scoped by organization and request identity.
4. Safe evolution tooling producing reviewable migrations/diffs without overwriting customized code.
5. Independent security assessment and operational qualification: backups/restore, load targets, incident procedures and deployment-specific threat modeling.
