# TryKatch Identity, Branding and Audit Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans task-by-task; preserve Sol ownership of security decisions and the single review workflow.

**Goal:** Extend the existing kernel's administrative workflows without replacing identity, RBAC, audit or browser authentication.
**Architecture:** Identity owns identity/session state; organization groups/brand remain organization-owned; support delegation is a separately approved host capability. Audit extensions retain structured safe events and append-only runtime boundaries.
**Tech Stack:** Existing ASP.NET Identity/OpenIddict/Data Protection, PostgreSQL RLS, shared React UI and E2/E4/E5/S1/S2 where needed.
**Spec:** [Design and D3/D4](../specs/2026-10-23-feature-parity-design.md); [roadmap](2026-10-23-feature-parity-plan.md).

## Global Constraints

Apply roadmap constraints. Paths below are relative to `templates/trykatch/`; new names are proposed. New Identity/RBAC tables use their existing owning schema and exact grants. Do not weaken MFA step-up, last-admin/Owner guards or existing invitation/permission ceilings.

## Review Focus

- Session revocation closes HTTP and realtime access; security-stamp behavior remains compatible.
- Group membership cannot grant permissions the acting administrator cannot delegate.
- Support delegation carries both initiator/effective identity and never grants a universal organization bypass.
- Organization branding cannot introduce executable URLs/HTML or poison global caches.
- Audit retention/masking cannot delete required immutable history or expose secrets in snapshots.

## A1: Add persistent session administration and optional password policy

**Files:** Create `src/Common/Trykatch.Identity/IdentitySessionRegistry.cs`, `IdentitySessionRecord.cs`, `PasswordPolicyService.cs`, identity forward migrations and Application `Identity/AccountSessions.cs`; modify Identity composition, authentication/security controllers and related cookie validation. Add `tests/Trykatch.UnitTests/AccountSessionTests.cs`, `PasswordPolicyTests.cs`, integration `AccountSessionIntegrationTests.cs`; proposed account-security view components/stories and `web/apps/web/e2e/account-sessions.spec.ts`.

**Interfaces:** Current-user list/revoke/revoke-other-sessions API, platform revoke under explicit separate permission and audit; identity session record binds issued session ID, current user, security-stamp epoch, creation/last-seen/expiry/revocation and bounded device metadata. Password policy offers history/expiry controls without changing their disabled/default status implicitly.

- [ ] Test per-device issuance, current/other-session revocation, logout-all, MFA/security-stamp rotation, session expiry and ID tampering. `RevokedSessionCannotUseHttpOrRealtime` asserts denial after authoritative validation; document the configured enforcement latency and invalidation mechanism.
- [ ] Migrate old cookies with an explicit expiry/reauthentication policy. Never infer a durable valid session solely from a cookie's random ID. Limit last-seen writes and device metadata retention without weakening revocation checks.
- [ ] Test password history, configured expiry/warning boundaries, reset/recovery, external-IdP accounts and policy-disabled compatibility. Use existing Identity hashing primitives; never retain plaintext passwords or invent compulsory expiry as a parity default.
- [ ] Verify `AccountSessionTests`, `PasswordPolicyTests`, `AccountSessionIntegrationTests`, directly affected `AuthenticationSecurityTests`, `AccountSecurityStepUpTests`, `ProductionIdentityContinuityTests`, focused UI/browser checks and common exit.

## A2: Add organization user groups with bounded role propagation

**Files:** New organization Domain `OrganizationGroup.cs`/membership/role assignment entities, Application `Organizations/OrganizationGroupUseCases.cs`, Infrastructure `Organizations/OrganizationGroupStore.cs`, organization model/migrations and permission invalidation; API `OrganizationGroupsController.cs`; UI group views in existing user-management feature. Add `OrganizationGroupTests.cs`, `OrganizationGroupIntegrationTests.cs`, component/stories/browser scenario.

**Interfaces:** Organization group CRUD, member/role assignment and effective-permission evaluation; proposed permissions `people.groups.read`, sensitive `people.groups.manage`, validated against the existing naming catalog before freeze. Groups are workspace-owned, never global ASP.NET Identity roles.

- [ ] Test adding/removing membership and group roles, direct-plus-group grant union, no duplicate grants, current grant ceiling, protected Owner handling and cross-organization IDs. Assert no administrator can use a group to delegate authority they cannot grant directly.
- [ ] Update the one effective-permission resolver and authorization/invitation paths instead of introducing a second RBAC engine. Re-evaluate on membership/role/group changes; invalidate active sessions/realtime according to the established authority contract.
- [ ] Render friendly permission/role labels and group membership flows using existing UI controls; test partial failures, conflict versions, inaccessible groups and direct URL denial.
- [ ] Verify `OrganizationGroupTests`, `OrganizationGroupIntegrationTests`, affected `RoleValidationTests`, `AccessManagementBoundaryTests`, `InvitationActivationRlsTests` and user-management component/browser tests; common exit.

## A3: Implement governed support delegation

**Files:** Proposed Application `Identity/SupportDelegation.cs`, Identity/platform-owned grant store/protector, Infrastructure `Execution/SupportDelegationValidator.cs`, API `SupportDelegationController.cs`, explicit session/context integration and persistent UI banner. Add `SupportDelegationTests.cs`, `SupportDelegationIntegrationTests.cs` and `web/apps/web/e2e/support-delegation.spec.ts`.

**Interfaces:** Explicit create/start/revoke/end grant operations under separate sensitive `platform.support.delegate` permission (catalog validation may require splitting resource/action consistently). Grant binds initiator, effective actor, target organization, allowed operations, reason, expiry and revocation; D4 fixes bounds and approval policy before implementation.

- [ ] Record D4 and D1 authority mapping. If policy is unresolved, keep this task blocked while A1/A2/A4/A5 proceed. Do not substitute "log in as anyone" for a scoped grant.
- [ ] Test denied organization/action, expired/revoked grant, missing recent assurance, user/organization suspension, attempted nested delegation and session replay. Assert platform membership alone is insufficient and no grant adds an Owner membership or changes runtime-role RLS.
- [ ] Audit initiator/effective actor/organization/reason for every delegated action, show a persistent visible banner, provide explicit exit/revoke and ensure background work cannot outlive the approved delegation policy.
- [ ] Verify named delegation tests, directly affected auth/RBAC/session/async tests, browser visibility/end flow and common exit. Treat this as security-sensitive lead-owned design/implementation review.

## A4: Add validated organization branding

**Files:** Organization-owned `OrganizationBrand` settings/model/migration, Application/Infrastructure settings services and API; extend existing `web/apps/web/src/views/OrganizationSettingsPage.tsx`, `branding.ts`, shell appearance and email template brand resolver. Add `OrganizationBrandTests.cs`, `OrganizationBrandIntegrationTests.cs`, existing settings/branding tests/stories and an organization-brand browser scenario.

**Interfaces:** Read effective organization brand with version and product-wide fallback; update only under a validated organization settings permission. Logo references use Documents/approved assets when installed; colors and labels use constrained tokens, not arbitrary CSS/HTML.

- [ ] Test organization A/B isolation, product fallback, permission denial, invalid asset/URL/color, brand update cache invalidation and organization switch. One organization's branding cannot alter another's browser/email cache entry.
- [ ] Preserve application namespace/display-name behavior and build-time product brand as fallback. Render approved organization brand only in established organization context; global login/security messages keep the appropriate product/identity origin.
- [ ] Verify focused brand/unit/integration/settings/browser tests plus existing `branding.test.ts` and `OrganizationSettingsPage.test.tsx`; generation/typecheck/build and common exit.

## A5: Extend audit investigation, safe diffs and explicit retention

**Files:** Extend `src/Common/Trykatch.Application/Auditing/`, Infrastructure `Persistence/AuditWriter.cs`, `AuditReader.cs`, immutable intent projection and Identity security event read seam; proposed `AuditRedactionPolicy.cs`, `AuditRetentionJob.cs`, API investigation endpoints and UI. Add `AuditRedactionTests.cs`, `AuditRetentionTests.cs`, host `AuditInvestigationIntegrationTests.cs`.

**Interfaces:** Module-declared safe before/after diffs feed the existing structured vocabulary; one investigation query contract supports organization/actor/time/action/target/correlation and approved security events. Redaction occurs before persistence/telemetry, not only in the browser. Retention runs only under D3's explicit authority/policy.

- [ ] Test credential/token/PII fields, nested payloads, unknown event types, permission failures, large diffs and body-capture exclusions. Assert sensitive values are absent from database snapshots, logs and exports; existing safe audit titles remain readable.
- [ ] Add opt-in safe entity diff mapping and bounded HTTP metadata capture; never enable blanket request/response bodies or replace durable audit intents with a lossy channel. Preserve business/audit/outbox atomicity.
- [ ] Add authorized security-event investigation with a separate identity-store read boundary; organization viewers cannot traverse global identity events. Exports reapply current scope and safe fields.
- [ ] Retention requires approved windows/legal hold, dry-run counts, dedicated narrow maintenance authority, immutable operator audit and batch bounds. Ordinary runtime audit roles remain append-only; configuration absence performs no deletion.
- [ ] Verify `AuditRedactionTests`, `AuditRetentionTests`, `AuditInvestigationIntegrationTests`, existing `AuditIntentAtomicityTests`, `SafeRequestLoggingTests`, event-definition/projection regressions and focused UI/browser evidence; common exit.

## Phase exit

- [ ] Each independent task has current-source authorization, migration and UI evidence; no claim that an unresolved delegation/retention policy is implemented.
- [ ] Record evidence in proposed `docs/specs/evidence/parity-administration.md`; retain unchanged baseline MFA/invitation guarantees explicitly.
