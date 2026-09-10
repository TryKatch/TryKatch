# Module data isolation

Trykatch treats persistence ownership as part of a module's executable contract. A data-capable module must declare a default ownership mode and every relation it owns. Organization-owned resources name their CLR entity and PostgreSQL isolation policy. Non-organization resources require a closed access profile: `platform-only` in `platform`, `identity-only` in `identity`, `global-read-only` in `reference`, or `host-only` in `infrastructure`. Unsupported combinations fail validation; modules cannot opt into the host's outbox exception.

At startup, `ApplicationDbContext` composes only enabled model contributors. Every entity implementing `IOrganizationOwned` receives the named `OrganizationIsolation` query filter automatically. Model validation then fails startup when a declared relation is absent from the model, maps to the wrong entity, lacks a non-null `Guid OrganizationId`, lacks the named filter, or lacks a tenant-first index. Module SQL validation rejects undeclared tables and requires organization tables to enable and force RLS with both `USING` and `WITH CHECK` clauses.

The migrator is the only schema owner. After applying host and module migrations it inspects PostgreSQL catalogs and fails the rollout when:

- a declared relation or policy is missing;
- organization RLS is not enabled and forced;
- an organization table has an extra policy, a wrong policy command/role/mode, or a `USING`/`WITH CHECK` expression other than the complete host-approved organization equality;
- a policy contains the removed `app.platform_admin` session bypass;
- any persistent relation in a non-system schema lacks an installed ownership declaration;
- any user-schema function is present, or a runtime role can execute a privileged function;
- a runtime role has administrative attributes, role memberships, object ownership, schema/database creation, dangerous table/column/sequence grants, or inherited access through `PUBLIC`; or
- effective schema, relation, sequence, column, or function access exceeds the declared profile or the exact host control-plane contract.

The migrator persists ownership declarations in `platform.module_data_resources`. Generated registries retain inert resource metadata for all installed modules while loading executable contributions only for enabled modules. Disabling or unregistering a module does not erase its installed schema contract; retained tables remain inspected. Changing a persisted ownership declaration requires an explicit reviewed migration rather than a silent overwrite.

The generated database qualification derives its cases and foreign-key metadata from every enabled resource declaration. It migrates the host and modules, synchronizes and validates the installed catalog, provisions the same four runtime roles and access profiles as the migrator, revokes `PUBLIC`, and requires the full PostgreSQL inspector to pass before fixture assertions run. It reads referenced-key values from the generated target rows, including composite or unique non-`Id` keys, and executes every supported cross-organization attachment attempt; missing fixture metadata fails qualification explicitly instead of counting as a blocked attachment. It then proves default deny without context, organization A visibility, organization B write and cross-organization attachment rejection, immutable application organization context, and EF filter composition for Projects and the independently packaged Documents module.

## Runtime roles

Production uses four named connections with different PostgreSQL roles:

| Connection | Required role | Scope |
|---|---|---|
| `trykatch-organization` | `trykatch_org_runtime` | declared module data; tenant/actor-scoped RBAC and directory; declared global reads; append audit and outbox |
| `trykatch-platform` | `trykatch_platform_runtime` | organization/placement control plane; scoped role/invitation provisioning, no membership grants |
| `trykatch-identity` | `trykatch_identity_runtime` | identity and OpenIddict records |
| `trykatch-outbox` | `trykatch_outbox_worker` | select and update outbox records |

Production startup rejects a missing connection, a reused role, or a role name outside this contract. Organization authorization and administration resolve through the organization connection, including tenant-owned control-plane tables. Organization request middleware sets only transaction-local organization and actor context. Platform permission endpoints use the fixed platform role but receive no tenant RBAC policy bypass; there is no user-controlled or session-variable platform bypass.

Modules receive `IOrganizationModuleData`, not host DbContexts. That seam exposes organization-scoped queries and writes plus audit/outbox recording in the same application transaction. It does not expose platform, identity, placement, role, connection-string, or migration-owner access.

## Placement foundation

`IOrganizationDataPlacement` is the control-plane seam for immutable `Shared` or `Dedicated` placement. Provisioning records persist `Provisioning`, `Ready`, or `Failed`, support idempotent retries, and publish a route only after the adapter succeeds. Shared placement must pass the PostgreSQL isolation inspector before becoming ready.

Dedicated placement deliberately fails closed until a deployment supplies `IDedicatedPostgresProvisioner`. No platform API or UI advertises dedicated isolation in the template. Shipping that control requires a provider adapter that creates a separate database, stores only a secret reference, runs migrations and inspection, records evidence, and returns `Ready` before invitations are issued.

## Package trust

Package install and upgrade verify all supply-chain evidence before mutating the workspace. The publisher must be allowlisted and explicitly enrolled with a NuGet signer fingerprint, RSA attestation key, builder identity, and freshness limit. Verification authenticates the NuGet signature, signed in-toto/SLSA provenance, artifact identities, SPDX SBOM relationships, pinned hashes, and the trusted builder's signed vulnerability result. The vulnerability result is an attestation, not an independent live scan. See [Package publisher trust](module-package-trust.md) for the evidence contract and enrollment procedure.

Installation restores from verified byte snapshots through an exact local NuGet source and a fresh package cache, and references the verified local npm archive. Post-restore hashes and lockfile integrity bind installed artifacts to those verified bytes. Compatibility and contribution checks remain mandatory. Verification failures precede workspace mutation; installation failures restore captured catalog, configuration, project, registry, and lockfile state.

Workspace modules are source-reviewed and use `module register`; they do not claim package-signature verification. The included Documents module is the full-stack workspace proof and is structured as packable .NET and npm packages. A release pipeline still needs an organization-controlled signing certificate and provenance/SBOM attestation service before publishing it as a package-distributed artifact.

## Release gate

Run from the generated solution root:

```bash
dotnet build Trykatch.slnx --locked-mode
dotnet test tests/Trykatch.UnitTests/Trykatch.UnitTests.csproj --no-build
dotnet test tests/Trykatch.IntegrationTests/Trykatch.IntegrationTests.csproj --no-build
dotnet run --project tools/Trykatch.ModuleTool --no-build -- module doctor --root .
pnpm --dir web typecheck
pnpm --dir web test
pnpm --dir web build
```

A release candidate is blocked by any schema-inspection error, generated isolation failure, catalog drift, unsigned/untrusted package, missing supply-chain artifact, shared runtime role, unresolved dedicated-provisioning evidence, or high/critical security finding.
