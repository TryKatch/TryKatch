# ADR 0021: Installed feature availability controls

Status: Accepted; implemented and locally qualified; exact-head CI and merge pending.
Date: 2026-10-24.

## Context

Module installation and authorization answer different questions. Installation determines which module declarations, routes, and UI contributions are composed into a deployment. Permissions and organization membership determine what an actor may do. Operators also need a reversible way to make a declared capability unavailable in an environment or for one organization without changing installation, permissions, or stored business data.

Availability must apply to HTTP requests and non-HTTP entry points alike. A cached project response or an idempotency receipt must not bypass a newly disabled read or write. Browser state must fail closed when the current organization's feature snapshot is missing, mismatched, or unavailable.

## Decision

Modules declare features explicitly in their manifest and runtime descriptor. A declaration has a stable owner-prefixed key, a name, a description, and an explicit Boolean default. Declaration metadata is checked across generated backend and Web registries. A declaration describes a capability; it does not grant a permission, membership, entitlement, or installation.

The host evaluates a feature only when its owning module is installed and declares that key. Effective availability follows this order:

1. An absent declaration or deployment hard-disable makes the feature unavailable.
2. A platform force-disable makes it unavailable.
3. A nullable organization override takes precedence when present.
4. Otherwise the nullable environment control applies.
5. Otherwise the configured default applies.
6. Otherwise the declaration's explicit default applies.

Any hard-disable veto wins. For example, `projects.reads` and `projects.writes` are separate availability controls and are distinct from `projects.read` and `projects.manage` permissions.

The environment name comes from the server's `IHostEnvironment.EnvironmentName`, is normalized to lowercase invariantly, and must contain 1–64 ASCII letters, digits, dots, underscores, or hyphens, beginning with an alphanumeric character. Clients cannot choose an environment. Immutable startup configuration uses `Features:Disabled` for deployment hard-disables and `Features:Defaults` for configured Boolean defaults. Configuration is validated at startup; well-formed values for features whose module is absent do not install or authorize that module.

Platform operators use the feature administration API or Platform Features page. `platform.features.read` permits listing controls and reading history; `platform.features.manage` permits saving them. Neither permission grants `platform.tenants.read`, organization membership, nor any Projects permission. The optional organization target is available from an authorized organization-directory selection or an existing organization link. A linked target does not expose the organization directory. Platform feature management can set that target without creating membership in the organization.

Environment defaults and organization overrides are nullable. Resetting one restores inheritance; it is saved as a new version rather than deleting its control row. Every save supplies the version last displayed by the operator. `Guid.Empty` is valid only when no row exists. A stale version returns a conflict; target contention returns a bounded busy response. Both require an explicit reload before the operator submits again. The client never retries a save automatically or silently adopts a new version for a draft. Each successful save appends typed before-and-after data to an immutable history row in the same transaction. Control and history rows are retained across module disablement and re-enablement.

The control records live in the `platform` schema: `feature_environment_controls`, `organization_feature_overrides`, and `feature_control_history`. The platform runtime can read and change control rows and append history. The organization runtime can read safe environment policy and only its current organization's override. Forced row-level security and exact grants enforce those boundaries. Runtime roles cannot delete controls or update history; identity, outbox, and work-coordinator profiles receive no access. The one-shot migrator creates the relations and grants the runtime profiles their exact access; the API never receives the migrator connection.

The authoritative evaluator reads current policy from PostgreSQL on each server evaluation and does not cache enabled decisions. The current organization is resolved by the server. HTTP feature metadata is an early boundary, while application use cases also evaluate availability so direct calls, assistant reads, and queued execution receive the same protection. Reads and writes are checked independently. A successful mutation receipt is checked against current write availability before it is disclosed. A policy-store failure returns `503 feature_policy_unavailable`; a disabled feature returns `409 feature_unavailable`; missing permission remains `403`. Existing committed event production and retained data are not removed when a feature is disabled. Realtime admission, live delivery, and module history visibility follow the feature's read availability, while an absent module continues to use its ordinary missing-route behavior.

The Web SDK selects a snapshot only when it succeeded and its organization ID matches the current organization. Missing declarations, unknown keys, stale workspace snapshots, loading, and errors all evaluate to unavailable. The organization snapshot refreshes on focus and every 30 seconds. The API remains authoritative if a browser still has a rendered control when availability changes.

## Consequences

### Positive

- Operators can independently pause reads and writes by environment or organization without changing role grants or module composition.
- Current server evaluation protects direct, HTTP, replay, assistant, queued, and realtime paths rather than relying on hidden browser controls.
- Versioned writes and immutable history make control changes auditable and safe under concurrent operator sessions.
- Disabling a feature or module retains control history and business data for a later re-enable.
- The existing five-role PostgreSQL boundary is retained; the feature uses existing platform and organization profiles.

### Negative

- Current policy evaluation depends on PostgreSQL availability and can return a safe 503 instead of serving from a stale enabled cache.
- Browser affordances may take up to the refresh interval to reflect a change, though server-side checks apply immediately and reject stale submissions.
- Operators need the new explicit platform permissions and must reload after conflicts or control contention.

## Alternatives considered

- **Browser-only feature flags:** rejected because direct HTTP, replay, assistant, and queued calls would bypass them.
- **Permission grants as feature switches:** rejected because temporary availability changes would mutate authorization and could grant access to actors without the required permission or membership.
- **Deleting an override to reset it:** rejected because a later write could not distinguish a missing control from a previously existing one, and history would lose the retained target identity. Nullable values with a new version preserve inheritance and prevent an ABA-style reset.
- **Cache enabled values:** rejected because revocation and policy changes could remain stale, and cache availability would become part of access control. Database failure is explicitly fail-closed.
- **Removing module installation to pause one feature:** rejected because module installation controls composition and route existence, not the reversible per-feature policy.

## Evidence and limits

The implementation contracts are recorded in the feature availability plan and [feature parity contract](../specs/feature-parity-contracts.md). Operator procedures are in [feature availability operations](../operations/features.md). Targeted backend/SDK/UI, renamed React/backend-only packed products and the corrected production-browser lifecycle are qualified in the [S4 evidence](../specs/evidence/parity-platform-services.md), including the single integrated review and its regression fix. Exact-head CI/merge, deployment and release qualification remain separate gates. It does not change the scope of S5, S6, future entitlements, or future permission reconciliation.
