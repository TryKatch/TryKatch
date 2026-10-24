# Installed feature availability

[Français](features.fr.md)

Feature availability lets an authorized platform operator pause a declared, installed module feature for the current deployment environment or one organization. It is independent of permissions, membership, entitlements, and module installation. A feature control never grants a user access that the existing authorization checks do not allow.

## Before deployment

Apply platform migration `20261024160455_AddFeatureAvailabilityControls` with the existing one-shot privileged migrator before deploying an API that uses feature controls. The migration creates `platform.feature_environment_controls`, `platform.organization_feature_overrides`, and `platform.feature_control_history`, with forced row-level security and grants for the existing platform and organization runtime profiles. The platform profile manages controls and appends history; the organization profile reads safe environment policy and only its current organization's override. Runtime roles cannot delete control rows or update history. Identity, outbox, and work-coordinator profiles receive no access. Do not add a runtime role or provide the migrator connection to the API. Follow the [database migration workflow](../database.md).

Deployment policy is immutable for a running process and is supplied through configuration:

```text
Features__Disabled__0=projects.reads
Features__Defaults__projects.writes=false
```

`Features:Disabled` is a list of deployment hard-disable feature keys. `Features:Defaults` maps feature keys to Boolean defaults. These settings are validated at startup. They apply only to declared features in installed modules; a retained value cannot install a removed module or make an undeclared feature available. Changing these settings requires the normal configuration update and process rollout. Platform and organization controls are persisted separately in PostgreSQL.

The environment is taken from the server's `IHostEnvironment.EnvironmentName`; clients cannot select it. The name is normalized to lowercase invariantly and must be 1–64 ASCII letters, digits, dots, underscores, or hyphens, starting with an alphanumeric character.

## Precedence and independent controls

Each module feature declaration has a stable owner-prefixed key, name, description, and explicit Boolean default. Effective availability is evaluated in this order:

1. The owning module must be installed and declare the feature. A deployment hard-disable makes it unavailable.
2. A platform force-disable makes it unavailable.
3. A non-null organization override wins.
4. Otherwise a non-null environment control applies.
5. Otherwise the configured default applies.
6. Otherwise the declaration default applies.

A force-disable or deployment disable is a veto over every enabled value below it. `projects.reads` and `projects.writes` are independent availability controls; they do not represent `projects.read` or `projects.manage`. The actor must still have the required permission, and organization endpoints still require valid membership and organization context.

## Platform controls

Open **Platform administration → Feature controls**. The current environment is shown by the server. A read grant can list declarations, inspect controls, and view paged immutable history. A separate manage grant is required to save controls. The feature permissions do not grant organization-directory access or organization membership. Use the paged organization selector only when the session also has `platform.tenants.read`; an existing organization administration link can open a specific target without granting directory browsing.

The platform API uses these operations:

| Operation | Permission | Purpose |
| --- | --- | --- |
| `GET /api/v1/platform/features` | `platform.features.read` | List declared features and current environment/optional organization controls |
| `GET /api/v1/platform/features/{featureKey}/history` | `platform.features.read` | Read a bounded page of immutable history |
| `PUT /api/v1/platform/features/{featureKey}/environment` | `platform.features.manage` | Save an environment default and force-disable state |
| `PUT /api/v1/platform/features/{featureKey}/organizations/{organizationId}` | `platform.features.manage` | Save an organization override |

The PUT operations use the normal cookie and antiforgery protections. The current organization's application snapshot is `GET /api/v1/features`; it does not accept an organization or environment selector from the caller.

Environment defaults and organization overrides each have three states: enabled, disabled, or inherited. Inheritance is represented by `null`. Saving inheritance creates or updates a versioned control row; it does not delete the row. The environment force-disable is a separate veto and is cleared explicitly.

Every save includes the version currently displayed by the operator. Use `Guid.Empty` only for a target with no stored control row. A successful save advances the version and appends an immutable before-and-after history record in the same database transaction. Do not delete rows or edit history to reset a control.

If a save reports `409 feature_control_conflict`, another operator changed the control after it was loaded. If it reports `409 feature_control_busy`, another transaction currently holds the control target. In either case, reload the controls, review the current values, and then deliberately resubmit if still appropriate. The interface preserves the draft and does not retry the save or silently replace its expected version. Do not script blind save retries.

## Runtime behavior and recovery

The server evaluates feature policy from PostgreSQL for the current organization and does not use a cached enabled decision. HTTP endpoints and their application use cases both enforce availability. The same use-case check applies to direct application calls, assistant reads, and queued execution. A completed idempotency receipt is subject to current write availability before its stored response can be returned. Read and write controls remain independent.

| Result | Meaning | Operator or client action |
| --- | --- | --- |
| `409 feature_unavailable` | This declared feature is disabled for the current environment or organization. | Check the environment and organization controls and their precedence; restore the needed control only if authorized. |
| `403 forbidden` | The actor lacks the required permission or organization authority. | Review the existing role and membership grants. Enabling a feature does not change them. |
| `503 feature_policy_unavailable` | The current policy could not be read from its authoritative store. | Restore database connectivity and retry the operation after service health recovers. Do not enable a stale fallback. |

The Web SDK hides feature-gated routes and controls unless a successful snapshot for the current organization includes the declared key as enabled. It fails closed during loading, after an error, for an unknown key, or when the snapshot organization does not match the selected workspace. The snapshot refreshes when the window regains focus and every 30 seconds. The server remains authoritative: a request rendered before a disable can still be rejected when submitted.

Disabling a feature or module does not delete committed business data, feature control rows, or feature history. An absent module keeps its ordinary missing routes and UI contributions; enabling a feature control cannot bring the module back. Re-enable the module through the normal module lifecycle, then review its retained controls before enabling its features. See [module lifecycle guidance](../modules.md#lifecycle-commands).

For the control-plane decision and data boundaries, see [ADR 0021](../adr/0021-installed-feature-availability.md).
