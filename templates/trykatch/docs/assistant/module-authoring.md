# Extending and creating modules

## Find the supported seam

Start at AGENTS.md, docs/development/backend.md and the owning module's manifest. Use the application-local module tool, not an unrelated globally installed version: dotnet run --project tools/Trykatch.ModuleTool -- module facts [module-id]. Facts report validated installed metadata and source entrypoints. module doctor validates composition. Do not infer symbols from a different application or a vendor's latest documentation.

For an existing module, modify application-owned source manually. Do not run module create over customized code or regenerate source from a retained blueprint. For a packaged module, use a published extension point or an explicitly reviewed source-ejection workflow. Never silently patch package caches. Cross-module dependencies consume IntegrationEvents or a named extension contract, not another module's implementation projects.

## Create and verify

Use module create --help to inspect the installed generator. Organization-owned CRUD is supported through field declarations; supported workflow transitions use a validated JSON blueprint. A status enum alone does not enforce guards, reservation overlap or concurrency. Unsupported cross-record rules need focused application/domain logic and transaction tests. Add --with-web only when the generated application has the web host installed.

Declare stable module IDs, permissions, operation IDs, ownership resources and every persistent relation. Regenerate explicit registries after composition changes. Add forward-only migrations; never edit an applied migration or delete stored data when disabling a module. Rebuild OpenAPI and regenerate the TanStack client for API changes. Generated registries/clients are machine-owned; domain and module UI source are customizable.

## Add a realtime invalidation

Realtime notifications are invalidation references. Declare a finite, versioned `execution.realtimeAudiencePolicies` entry in the module manifest: list only its supported audience kinds, owned resource keys, recipient permissions, exact event/schema versions and allowed producer operations. Organization audiences require a recipient permission. Bind each installed policy to exactly one scoped `IModuleRealtimeAudiencePolicy` adapter with `ModuleRealtimeAudiencePolicyBinding`; registration does not scan for adapters. The adapter must check the current organization and actor, permission, and any resource selector against the module's current read visibility. Projects is the reference: `ProjectsReadAudiencePolicy` checks `projects.read` and resolves resource visibility through `IProjectStore`.

For a system producer, declare a `committed-event` operation and a finite system grant whose `allowedOperationKeys` includes that operation and whose `allowedRealtimePolicies` names the exact owner, policy key and version. The policy must reciprocally list the same producer module/operation/version in `allowedSystemProducers`. Cross-module producers also declare a required or optional dependency on the policy owner. Only publish from the registered event consumer for the committed source event: `IModuleRealtimePublisher.PublishAsync` appends history as part of that consumer's existing transaction, so the invalidation is committed with its inbox receipt. The neutral `ModuleRealtimeNotification` carries the module/event/version, event ID and `ModuleRealtimeAudience`, not business payload, group, organization ID or cursor. See Projects' `projects.created-realtime` consumer and `projects.created-realtime` grant for the complete manifest/adapter pattern.

For a web module, add a `WebRealtimeInvalidationContribution` to its module definition. Its finite `subscription` selects the registered policy/audience; `matches` filters the exact module event/schema version; `queryKeys` lists the module's owned TanStack Query keys to refetch. Do not move cursor or history ownership into the module. The host wakes reconciliation from pushes and repairs through HTTP state/history; see [authorized realtime operations](../operations/realtime.md) for browser recovery, deployment and runtime behavior.

After changing the catalog manifest or backend descriptor, regenerate the explicit registries and validate composition from the application root:

```sh
dotnet run --project tools/Trykatch.ModuleTool -- module generate
dotnet run --project tools/Trykatch.ModuleTool -- module doctor
```

The CLI validates finite policy/grant declarations; runtime composition additionally requires exactly one adapter for every installed policy. Manifest facts alone do not prove that an adapter is registered. The [frozen S2 contracts](../specs/feature-parity-contracts.md#7-cache-and-realtime--s1s2) define the neutral boundary.

## Test layers

Use a falsifiable feature specification, domain/use-case tests, architecture checks and relevant HTTP acceptance cases. For ownership/isolation claims run real PostgreSQL with the non-owner runtime role; check missing scope, foreign record IDs, denied writes, audit/outbox atomicity and conflicts. For UI changes run typecheck, tests, production build and a browser flow at phone/tablet/desktop widths with loading, empty, denied and recovery states.

Project-local trykatch-spec, trykatch-build-module, trykatch-extend-module, trykatch-review and trykatch-verify skills route this workflow. Skills help a coding agent work in the repository; the product AI Help chat does not execute those commands or modify code.
