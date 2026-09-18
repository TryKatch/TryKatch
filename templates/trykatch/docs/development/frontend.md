# React module work

First check for `web/package.json`. Backend-only applications do not have the host, SDK workspace, or frontend toolchain; skip frontend commands there. A module-local `Web` directory alone does not mean a web host is installed.

Every frontend task starts by consulting [Storybook](../storybook.md) and relevant colocated stories. Use actual components and brand tokens as the reference; maintain stories for changed visual behavior and applicable validation, loading, empty, error, denied and conflict states. Check affected interactions and accessibility plus light/dark, English/French and relevant responsive layouts. Catalogue checks do not replace backend or real application checks.

Read [frontend architecture](../frontend-architecture.md) and the web sections of [module authoring](../modules.md). Then inspect actual exports and reference code:

- `web/packages/module-sdk/src/index.ts` and `extensions.tsx`: module, route, navigation, and extension contracts.
- `web/packages/ui/src/primitives.tsx`, `patterns.tsx`, and Storybook examples: reusable UI behavior.
- `src/Modules/Projects/Web/module.ts` and `ProjectsPage.tsx`: a registered module surface.
- `web/apps/web/src/module-overrides.ts`: application-owned web composition overrides.
- `web/apps/web/src/features/archive/archiveResources.ts`: recovery integration; inspect the installed SDK's version-aware contract before adding it.

Use a module's declared entrypoint rather than assuming all `Web` packages have the same file layout. The CLI owns generated `web/apps/web/src/modules.ts`; change manifests/overrides and regenerate instead of editing it.

Use the generated API client and existing authentication/antiforgery helpers. Check client exports before choosing an import. Browser storage must not contain access or refresh tokens. UI permissions control affordances; the API remains responsible for authorization.

Keep business constraints on the server and expose useful validation errors. Reuse `DataTable` behavior and existing form/dialog components. Include loading, empty, error, denied, and conflict states where relevant. Preserve user input on conflicting updates, and do not blindly retry a write with a newer version. User-facing module messages need English and French entries in the existing message catalog shape.

New or edited text, email, password, search, numeric, date and multiline fields must use the shared `FloatingInput`, `FloatingTextarea`, `PasswordField` or `SearchField` from the UI package. Use `FloatingSelect` for branded dropdowns. Never duplicate their labels, add label backgrounds or extra focus borders, or override their standard height (38 px; search 34 px). Keep visible labels on native file inputs, checkboxes, radios and existing native selects. Their associated labels, error links, keyboard behavior and focus styles are documented and regression-tested in **Forms → Floating fields / Floating dropdown**. Cover changes in colocated Storybook interaction and accessibility tests, including the host view where cascading styles can differ. Zod improves immediate feedback; application-layer validators and domain rules remain authoritative.

Use server filtering/pagination for unbounded records. Keep decimal/64-bit integer API values as strings and normalize dates with the existing helpers. Recoverable modules use the Archive extension surface; ordinary lists show active records.

Build the API before regenerating its client. Install frontend dependencies before typecheck/tests/build. The [verification guide](verification.md) maps these checks and explains browser verification; compile success alone does not establish a working interaction.
