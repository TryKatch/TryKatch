# TryKatch Documents and Attachments Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans task-by-task; retain the project's worker/reviewer ownership rules.

**Goal:** Extend Documents into reusable, owner-authorized attachments while preserving private API-mediated storage.
**Architecture:** Documents owns metadata/storage lifecycle; consumer modules register owner policies through shared host contracts. Existing standalone documents and owner-bound attachments have different visibility rules.
**Tech Stack:** Existing S3-compatible/local storage, RLS, module layers and React components; scanner behind a reviewed adapter.
**Spec:** [Design and supported combinations](../specs/2026-10-23-feature-parity-design.md); [roadmap](2026-10-23-feature-parity-plan.md).

## Global Constraints

Paths below are relative to `templates/trykatch/`; all new paths are proposals. Apply roadmap constraints. E2/E4 are prerequisites for background cleanup/scanning; S5 precedes enforced storage quotas. D3 gates destructive retention and required-scanning policy; D7 stays deferred.

## Review Focus

- Private Chat/Catalog attachments cannot be discovered through the existing standalone Documents routes.
- Unknown/disabled owner policy, unscanned content and cross-organization references fail closed.
- Storage effects and database rollback are reconciled without exposing orphaned objects.
- Disabling a module preserves its files and prevents attachment access through another route.
- Archive, deletion request and permanent retention purge remain distinct actions.

## F1: Add opaque attachment contracts and owner authorization

**Files:** Create `src/Common/Trykatch.Modules.Abstractions/Attachments/ModuleAttachmentContracts.cs`, `AttachmentOwnerPolicy.cs`; extend `src/Modules/Documents/Trykatch.Modules.Documents.Domain/Document.cs`, Application `DocumentsUseCases.cs`, Infrastructure `DocumentsModule.cs`/model/migrations, Presentation `DocumentsEndpoints.cs` and manifest. Create Documents Application `DocumentAttachmentService.cs`; add module unit `AttachmentAuthorizationTests.cs` and integration `DocumentAttachmentIntegrationTests.cs`.

**Interfaces:** Implement `IModuleAttachmentService` and registered `IAttachmentOwnerPolicy` in the shared contract. Commands carry an opaque document ID and allowlisted owner type/resource ID; derive organization/actor from host context. Return opaque attachment references, never raw keys or credentials.

- [ ] Test `StandaloneReadPermissionCannotRevealPrivateAttachment` across list, search, metadata, download, archive, restore and delete; a nonmember's result contains no private attachment title/size/content. Test cross-organization attach and unknown/disabled owner policy.
- [ ] Add explicit standalone versus owner-bound state and migrate existing documents to standalone. Enforce owner authorization in all old and new access paths, not only the new attachment service. Bind one attachment to its allowed owner or model an explicitly authorized multi-owner relation; never silently broaden access when attaching twice.
- [ ] Keep attachment linking and metadata/audit/outbox in the host organization transaction; validate owner existence/current access through registered policies. Use declared relationships/organization keys and retain existing rollback compensation.
- [ ] Verify module-local attachment/unit tests and `DocumentAttachmentIntegrationTests`, plus existing `DocumentsModuleTests`, `ObjectStorageIntegrationTests`, `ModuleAntiforgeryTests` and generic organization-isolation tests where changed. Common exit.

## F2: Add validation, quarantine, scanning and safe retention

**Files:** Create Documents Domain `DocumentScanState.cs`, Application `DocumentScanUseCases.cs`, Infrastructure `DocumentScannerAdapter.cs`, `DocumentRetentionJobs.cs`, `DocumentReconciliationJobs.cs`; modify existing storage adapter only through its owned seam. Add module tests `DocumentScanTests.cs`, `DocumentRetentionTests.cs` and integration `DocumentLifecycleIntegrationTests.cs`.

**Interfaces:** Produce a registered scanner contract with `Clean`, `Rejected`, `Unavailable` and retryable/terminal outcomes; registered job names `documents.scan.v1`, `documents.reconcile.v1`, `documents.retention.v1`. Owner and scan access remain authoritative on every download.

- [ ] Test spoofed media type/extension, size boundary, quarantine access, scanner outage, malicious verdict, duplicate scan result and stale object checksum/version. Assert only the current object's valid clean verdict can release required quarantine.
- [ ] Stream/hash/validate content with existing bounds; create durable scan/reconciliation intent and private quarantine state. Do not expose a document merely because object upload succeeded. Provide a disabled-scanning mode only when explicitly configured and described as unscanned.
- [ ] Test failed metadata commit after upload, failed delete, restart midway through cleanup, legal hold/configured retention and restoring an archived file. Reconcile pending objects and quota reservations idempotently; archive does not refund bytes until physical removal is established.
- [ ] Execute permanent purge only with D3's configured policy/authority, bounded batches, audit and recovery documentation. Ordinary organization/worker credentials receive no global RLS/retention bypass. Missing policy performs no deletion.
- [ ] Verify `DocumentScanTests`, `DocumentRetentionTests`, `DocumentLifecycleIntegrationTests`, directly affected storage/quota integration tests and common exit. Use fake scanner/S3 fixtures, not live paid services.

## F3: Add reusable attachment UI and package qualification

**Files:** Extend `src/Modules/Documents/Web/src/` with proposed `AttachmentPicker.tsx`, `AttachmentList.tsx`, associated tests/stories and module-local messages; modify module-sdk contracts only for a named public UI extension if needed. Add `web/apps/web/e2e/document-attachments.spec.ts`; update module README, template conditions and developer docs.

**Interfaces:** Consume generated attachment endpoints; publish a Documents-owned reusable UI contribution via the module SDK, not direct imports of private components from Catalog/Chat. API IDs/permission requirements match F1. Document task IDs use F1–F3; D1–D8 always identify the design's decision register.

- [ ] Cover upload, progress, scan pending/rejected/unavailable, retry, owner denial, quota exhaustion, empty list and archive/restore states in Storybook and focused component tests.
- [ ] Verify keyboard operation, visible labels, current shared floating controls, English/French copy and generated query invalidation. Browser UI never receives object keys or storage credentials.
- [ ] Browser case: upload to a private owner as user A; user B with `documents.read` alone sees neither the attachment in Documents nor its bytes; disable owner code and confirm access closes without deleting the object.
- [ ] Qualify backend-only use and `--storage false`: existing standalone UI reports adapter unavailability; Catalog/Chat prerequisites reject enablement without working storage. Keep the unrelated exporter flag `--documents` unchanged.
- [ ] Verify affected Documents Web tests, the named browser test, generated clients, typecheck/build and composition/isolation checks; common exit. Record evidence in proposed `docs/specs/evidence/parity-documents.md`.
