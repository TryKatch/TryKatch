# Frontend module structure

Trykatch keeps reusable interface behavior in the smallest package that can own it.

## Seams

- `src/Modules/<Module>/Web` owns a business module's routes, pages, tests, and frontend package metadata. Module code depends only on published workspace packages and host services exposed by the module SDK.
- `apps/web/src` is the application host. It composes the generated module registry, shell, platform surfaces, and host-wide providers; it does not reach into a module's internal implementation.
- `packages/module-sdk` is the stable frontend host contract for routes, navigation, extension points, archive resources, and runtime services such as localization. Modules must not import host-internal contexts.
- `apps/web/src/components` contains host-specific components such as the role editor and Trykatch mark.
- `packages/ui/src/primitives.tsx` contains low-level owned UI primitives.
- `packages/ui/src/patterns.tsx` contains deeper reusable modules that combine behavior and presentation.
- `packages/api-client/src/generated` is machine-owned. Generate it from OpenAPI with `pnpm generate`; do not edit generated files.

## Data table

`DataTable<T>` is the standard collection interface. A caller supplies typed rows, stable row IDs, and column definitions. The module owns:

- accessible table markup and sort state;
- stable client-side sorting and collection search;
- compact, comfortable, and spacious density;
- per-column visibility with at least one column always visible;
- empty results, result counts, toolbar layout, and responsive overflow.

Feature-specific filters belong in the `toolbar` slot. Use server filtering and pagination for unbounded collections; use the built-in search for bounded collections already loaded in memory. Column definitions should provide `sortValue` and `searchValue` rather than formatting data outside the module.

The Storybook example under `packages/ui/src/patterns.stories.tsx` is the reference usage.

For collections whose rows have large secondary detail, use the table's `renderExpandedRow` seam instead of adding chips or long text to summary cells. Supply `getRowExpansionLabel` for a specific accessible control name. The table keeps only one row expanded, closes disclosure when search, sorting, or paging changes, and can apply bounded client-side paging with `pageSize`. Feature code owns the detail content; the shared table owns the interaction.

## Archive recovery

`features/archive/archiveResources.ts` is the extension seam for recoverable modules. Each definition declares the record type, read and manage permissions, normalized loader, and resource-specific restore route. The Archive page owns cross-module discovery, filtering, recovery metadata, and restore affordances; normal feature pages load Active records only. Add a registry definition when a new module adopts `RecoverableEntity` instead of adding lifecycle selectors to its working table.

## Responsive contract

The application shell owns responsive navigation. Desktop and tablet layouts retain the persistent collapsible sidebar; phone layouts below 760px use an off-canvas drawer with an inert hidden state, backdrop dismissal, navigation dismissal, and Escape-key dismissal.

Pages must never create document-level horizontal overflow. Dense tables keep their semantic columns and scroll inside `table-wrap`; filter toolbars stack, primary page actions expand to the available width, multi-column forms become single-column, and dialogs stay inside the viewport with their long content scrolling internally. Validate shared surfaces at 320px, 768px, and 1440px before release.

## Audit activity

Audit event codes and their human presentation metadata are defined in the backend audit module. React consumes titles, descriptions, actors, targets, categories, and severity from the contract; it does not translate raw event codes. This keeps wording consistent for future web, CLI, notification, and export adapters.
