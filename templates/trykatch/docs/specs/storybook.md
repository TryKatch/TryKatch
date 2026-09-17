# Complete React Storybook catalogue

## Outcome and scope

React applications ship one branded catalogue of shared UI, public visual host modules and route views, existing business module pages, and generated CRUD/workflow pages. Native controls are documented as patterns, not replaced. Backend-only output excludes the web workspace. No paid service, automatic hosting, screenshot baseline gate, or new business/security behavior is included.

## Decisions and ownership

The approved plan supplies the component inventory, automatic `--with-web` stories, interaction/accessibility gates, responsive/translation requirements and develop-to-main release workflow. Storybook owns development-only configuration and fixtures; production modules retain their existing interfaces and behavior. API fixtures are not authorization or RLS evidence.

## Implementation

1. Foundation: a dedicated web workspace app discovers colocated stories, owns mock network boundaries, isolated providers, theme/language controls, and compatible browser tests. Existing unit tests remain unchanged.
2. Catalogue: stories cover foundations, all shared visual exports, host visual exports/views, form patterns and installed example modules. Public visual exports must have checked coverage; infrastructure providers are exercised through their consumers.
3. Generator: CRUD and blueprint Web output includes deterministic stories/fixtures based on source metadata; story verification joins the existing module transaction.

## Acceptance cases and confirmed test seams

- Workspace commands: `storybook`, `storybook:build`, and `storybook:test` resolve the single catalogue. Node contract tests and a real static build verify this interface.
- Story render/play: controls, forms, permissions, tables, dialogs and recovery operate using public UI interfaces. Browser interaction and accessibility checks verify these seams; API requests are mocked only at the network seam.
- Isolation: one story's mutations/cache/preferences cannot alter a subsequent story; unexpected API requests never reach a real server.
- Production: browser-test fixtures and worker assets do not enter the production web build.
- Generator CLI: full-stack CRUD/blueprint creation emits discoverable stories and includes them in verification/rollback. Backend-only creation emits none. Packed-template qualification verifies renamed namespaces and both web modes.
- Browser layout: light/dark, English/French, and 320/768/1440 px retain branding without document overflow.

## Verification record

Foundation and first catalogue slice implemented: one Storybook 10.6 workspace, real CSS, isolated query/router/translation/module/account-security providers, MSW fail-closed requests, static build, browser interaction/a11y gate, and English/French startup guides. Initial 58 stories pass Chromium interaction/accessibility checks. Shared badge/error/muted text contrast defects found by these tests were corrected using the existing brand hues and semantic theme tokens.

Passed locally: workspace command contract, React workspace typecheck, existing workspace unit tests, production build, isolated Storybook static build, 58 browser story tests, and English/French documentation-site build. A headed browser rendered the table column-settings interaction successfully with no console errors.

Still incomplete: full inventory enforcement, remaining host/module states and server pagination, generator CRUD/blueprint stories and transaction verification, fresh packed-template qualification, the complete responsive/theme/language matrix, promotion and coordinated publication. No release is claimed until published packages are verified.
