# Complete React Storybook catalogue

## Outcome and scope

React applications ship one branded catalogue of shared UI, public visual host modules and route views, existing business module pages, and generated CRUD/workflow pages. Native controls are documented as patterns, not replaced. Backend-only output excludes the web workspace. No paid service, automatic hosting, screenshot baseline gate, or new business/security behavior is included.

## Decisions and ownership

The approved plan supplies the component inventory, automatic `--with-web` stories, interaction/accessibility gates, responsive/translation requirements and develop-to-main release workflow. Storybook owns development-only configuration and fixtures; production modules retain their existing interfaces and behavior. API fixtures are not authorization or RLS evidence.

## Implementation

1. Foundation: a dedicated web workspace app discovers colocated stories, owns mock network boundaries, isolated providers, theme/language controls, and compatible browser tests. Existing unit tests remain unchanged.
2. Catalogue: stories cover foundations, all shared visual exports, host visual exports/views, form patterns and installed example modules. Public visual exports must have checked coverage; infrastructure providers are exercised through their consumers.
3. Generator: CRUD and blueprint Web output includes deterministic stories/fixtures based on source metadata; story verification joins the existing module transaction.

Additional approved catalogue/form work: wire Zod into actual organization role, project and document metadata forms; keep FluentValidation 12.1.1 authoritative for corresponding backend requests. Preserve permission enforcement, antiforgery, domain rules and API contracts. Add shared branded floating inputs/textareas and floating password fields, including associated labels, error/help links, disabled/read-only, autofill and date/time behavior. Native select/file controls retain visible labels. Maintain the shipped AI skills so every frontend task consults Storybook first. This slice does not claim every legacy form or generator uses Zod/floating fields yet.

## Acceptance cases and confirmed test seams

- Workspace commands: `storybook`, `storybook:build`, and `storybook:test` resolve the single catalogue. Node contract tests and a real static build verify this interface.
- Story render/play: controls, forms, permissions, tables, dialogs and recovery operate using public UI interfaces. Browser interaction and accessibility checks verify these seams; API requests are mocked only at the network seam.
- Isolation: one story's mutations/cache/preferences cannot alter a subsequent story; unexpected API requests never reach a real server.
- Production: browser-test fixtures and worker assets do not enter the production web build.
- Generator CLI: full-stack CRUD/blueprint creation emits discoverable stories and includes them in verification/rollback. Backend-only creation emits none. Packed-template qualification verifies renamed namespaces and both web modes.
- Browser layout: light/dark, English/French, and 320/768/1440 px retain branding without document overflow.

## Verification record

Foundation and expanded catalogue slice implemented: one Storybook 10.6 workspace, real CSS, isolated query/router/translation/module/account-security providers, MSW fail-closed requests, static build, browser interaction/a11y gate, and English/French startup guides. There are now 112 passing Chromium interaction/accessibility stories, including real module pages, role validation, transparent floating controls and search-filter dropdowns. Shared badge/error/muted text contrast defects found by these tests were corrected using the existing brand hues and semantic theme tokens.

Passed locally: workspace command contract, React workspace typecheck, workspace unit tests (including 41 host tests), production build with Storybook asset exclusion, isolated Storybook static build, 112 browser story tests, and English/French documentation-site build. Backend validation verification passed a solution build with no warnings/errors, 283 host unit tests, Projects/Documents unit tests and host/module architecture tests.

Role layout regression: floating fields exposed a flex/minimum-height overflow that let permission content overlap the footer. A failing real-browser geometry test now protects a single scrollable form body and persistent footer. Narrow saving also protects the search/clear-selection row from horizontal overflow. Headed Chromium checks passed dark validation, French validation, saving and server-error views at 320/768/1440 px (12 checks). Floating input interaction tests prove focus, filled blur, clear/blur and prefilled values; search controls retain 34 px height and button sizes are unchanged. This is targeted evidence, not the complete catalogue layout matrix or browser autofill-manager qualification.

Still incomplete: full inventory enforcement, remaining host/module states and server pagination, generator CRUD/blueprint stories and transaction verification, fresh packed-template qualification, the complete responsive/theme/language matrix, promotion and coordinated publication. No release is claimed until published packages are verified.

Floating-label reference correction: direct inspection of Starlink's email control confirmed a transparent label centred on a real border notch, using a decorative fieldset/legend. The earlier above-border implementation and its assertions were incorrect for the requested behavior. Shared input, textarea, password and search controls now use the real notch while retaining Trykatch tokens. The introduced 54 px height was not the original Trykatch size: the user-approved compact adjustment now uses 38 px standard inputs (2 px above the 36 px baseline), unchanged 38 px role inputs and 34 px search heights. Textareas respect native row counts and resizing rather than an imposed 94 px minimum; buttons are unchanged. Associated labels remain the only accessible names; decorative outlines are aria-hidden. Regression checks reject labels floating outside the border and verify compact sizes, transparent backgrounds, an open notch on focus/filled state, and notch closure after clearing and blur. English/French catalogue guides describe the corrected behavior.

Approved search-control slice: shared `SearchField` keeps the 34 px search input, leading search icon and optional inset filter button. Caller-owned controlled filter content opens in a non-modal portal, with outside/Escape dismissal and focus restoration. `DataTable.searchFilters` reuses it without merging Columns into the filter dropdown. Inputs use their existing border/label focus colours without an extra ring; button keyboard-focus indicators remain. Story interactions prove selection changes visible results, select-all behavior, dismissal and table integration; dark/French examples pass accessibility checks. Headed Chromium verifies inset-button alignment and viewport containment at 320/768/1440 px. A private local package generated a renamed `SearchProof` application containing the shared control, styles, export and DataTable integration. This verifies template inclusion, not the broader fresh-project/module qualification gate or NuGet publication. Domain filter definitions/counts/server queries remain feature-owned.
