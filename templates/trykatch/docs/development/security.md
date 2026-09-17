# Authorization and organization isolation

Use the existing security path when adding a capability. The authoritative decisions are [request-derived organization context](../adr/0008-request-derived-organization-context.md), [permission catalogs](../adr/0005-permission-catalog-rbac.md), [platform access](../adr/0009-platform-access-control.md), and [module data isolation](../module-data-isolation.md).

For an organization operation, trace authentication, workspace resolution, permission authorization, and the transaction that sets PostgreSQL `app.organization_id` and `app.actor_id`. Resource IDs supplied by the caller must not bypass that scope. Organization and actor IDs come from authenticated context, not editable HTTP fields.

Use the secured module HTTP interfaces from `src/Common/Trykatch.Modules.AspNetCore`. Application use cases must also enforce the applicable permission through the installed authorization seam so a worker or another caller cannot bypass endpoint-only checks. Follow the owning module's actual interfaces and the Documents reference; do not invent a second authorization framework.

For new stored data, declare ownership and every relation in the module descriptor/manifest. Organization entities participate in the central EF filter and PostgreSQL policies enable and force RLS with both `USING` and `WITH CHECK`. Test with the real non-owner runtime role, including a missing organization context and another organization's record. An in-memory store cannot verify RLS.

Keep mutation, required audit, and outbox behavior within the existing transaction contract. Account for non-database side effects using the host's compensation seam when appropriate. See [audit atomicity](../audit-atomicity.md) and [transactional outbox](../adr/0010-transactional-outbox.md). Preserve concurrency/version checks and test denied/failed writes leave no partial business state.

Choose permission IDs and default grants from the feature's actors and existing catalogs. UI hiding, ownership labels, and assistant descriptors do not grant access. Do not implicitly give ordinary members sensitive review/administration permissions.

Product AI integration is optional and separate from coding skills. `AssistantToolDescriptor` declares an allowlist entry; the generated assistant contract supplies schema and policy metadata. It does not implement model execution, approval UI, or an authorization boundary. Do not add write tools without the runtime confirmation, payload binding, audit, and recovery needed by the intended adapter. See [AI-assisted development](../ai-assisted-development.md).
