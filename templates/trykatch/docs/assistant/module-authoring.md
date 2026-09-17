# Extending and creating modules

## Find the supported seam

Start at AGENTS.md, docs/development/backend.md and the owning module's manifest. Use the application-local module tool, not an unrelated globally installed version: dotnet run --project tools/Trykatch.ModuleTool -- module facts [module-id]. Facts report validated installed metadata and source entrypoints. module doctor validates composition. Do not infer symbols from a different application or a vendor's latest documentation.

For an existing module, modify application-owned source manually. Do not run module create over customized code or regenerate source from a retained blueprint. For a packaged module, use a published extension point or an explicitly reviewed source-ejection workflow. Never silently patch package caches. Cross-module dependencies consume IntegrationEvents or a named extension contract, not another module's implementation projects.

## Create and verify

Use module create --help to inspect the installed generator. Organization-owned CRUD is supported through field declarations; supported workflow transitions use a validated JSON blueprint. A status enum alone does not enforce guards, reservation overlap or concurrency. Unsupported cross-record rules need focused application/domain logic and transaction tests. Add --with-web only when the generated application has the web host installed.

Declare stable module IDs, permissions, operation IDs, ownership resources and every persistent relation. Regenerate explicit registries after composition changes. Add forward-only migrations; never edit an applied migration or delete stored data when disabling a module. Rebuild OpenAPI and regenerate the TanStack client for API changes. Generated registries/clients are machine-owned; domain and module UI source are customizable.

## Test layers

Use a falsifiable feature specification, domain/use-case tests, architecture checks and relevant HTTP acceptance cases. For ownership/isolation claims run real PostgreSQL with the non-owner runtime role; check missing scope, foreign record IDs, denied writes, audit/outbox atomicity and conflicts. For UI changes run typecheck, tests, production build and a browser flow at phone/tablet/desktop widths with loading, empty, denied and recovery states.

Project-local trykatch-spec, trykatch-build-module, trykatch-extend-module, trykatch-review and trykatch-verify skills route this workflow. Skills help a coding agent work in the repository; the product AI Help chat does not execute those commands or modify code.
