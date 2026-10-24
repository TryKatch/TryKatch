# Trykatch Projects module

Reference organization-owned module for project lifecycle management. The package contains the Domain, Application, IntegrationEvents, Presentation, and Infrastructure assemblies and composes through `ProjectsModule`.

The five Projects mutation endpoints opt in to bounded response replay with an optional `Idempotency-Key` header. Current organization membership, `projects.manage` and module-owned resource gates are rechecked before a receipt is returned. Resource checks include archived and deletion-requested projects and do not add a separate read-permission requirement.

See [mutation idempotency operations](../../../docs/operations/idempotency.md) ([Français](../../../docs/operations/idempotency.fr.md)) for client retries, finite replay windows, migration requirements and excluded response types.
