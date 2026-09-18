# UI catalogue

Run from the generated application's root, the directory containing its solution and `trykatch.modules.json`:

```bash
cd web
corepack pnpm install --frozen-lockfile
corepack pnpm storybook
```

Open <http://localhost:6006>. Storybook uses fixtures: **do not start the API, Docker or Aspire**. This is a UI development catalogue, not a running business application. Backend-only projects do not include it.

Use the toolbar to switch between light/dark, English/French and phone/tablet/desktop viewports. The catalogue loads the real application stylesheet and its CSS variables. Native input stories are reusable examples, not an additional form library.

## Find forms and validation

**Application UI → Role management → Review selected grants** exercises keyboard expansion, selected-only filtering, hidden-selection retention and empty-filter recovery. Permission groups start collapsed for new roles; groups containing existing grants open when editing. Search reveals matching groups, without discarding selections. Bulk selection applies only to visible, grantable permissions. Zod validation, compact floating fields and the scrollable dialog body with its persistent footer remain unchanged.

Start with **Welcome → Catalogue guide**, then **Forms → Native controls** and **Forms → Validation**. Validation renders the real organization role editor: required-field errors, correcting input, saving, server failures with preserved input, and French/dark examples. **Module UI → Projects** includes the real create form, required-name validation and failed-save state. **Module UI → Documents** includes the upload form, file validation and uploading state.

**Forms → Floating fields** documents shared `FloatingInput` and `FloatingTextarea`: empty, filled, focus/typing, invalid, disabled, read-only, numeric/date and translated/themed states. Role, project and document metadata forms reuse these controls. Floating labels remain associated HTML labels, not placeholders; date/time labels always float. Native file pickers and selects keep visible labels. Pass translated `label`, `description` and `error` values to the shared controls. Do not manually recreate floating-label CSS in feature pages.

Organization role, project and document metadata forms use Zod 4.5.4 before submission. FluentValidation 12.1.1 independently validates organization roles, project create/update commands and document metadata on the backend. File safety, permission grants, uniqueness and business invariants remain server responsibilities. Other existing forms may still use native/custom validation; this does not claim universal Zod adoption.

Shipped AI skills and `AGENTS.md` require consulting Storybook before frontend work and maintaining stories for changed visual behavior.

Floating labels rest inside empty, unfocused fields and move onto the top border on focus. A decorative fieldset/legend opens a real notch around the label; the label stays transparent, without a painted background or a second focus ring. Filled fields keep the label in the notch after blur; clearing and leaving a field returns it inside and closes the notch. Shared table, collection, audit and permission search fields follow the same behavior, with their search icons retained. **Forms → Validation → Constrained height / Narrow saving** checks that the role editor scrolls without overlapping its persistent action footer.

Floating controls retain Trykatch's compact scale: standard inputs are 38 px (a small 2 px increase over the 36 px baseline), role inputs remain 38 px and search fields remain 34 px. Textareas follow their native row count and remain resizable instead of imposing a tall minimum. Floating labels do not change button sizing.

Generic host form styles must exclude labels owned by `.floating-control`; they must not turn a floating label into a grid or separate its required marker onto another line. **Forms → Floating fields → Host form contexts** checks text, textarea and password controls inside authentication, dialog, profile and role containers. **Application UI → Authentication → Sign in / French sign in / Dark sign in** checks the real sign-in page through focus, typing, blur and clearing. Keep these integration checks alongside isolated field stories when changing form CSS. This styling is shipped in the project template, so newly generated React applications use the same controls and rules.

**Forms → Floating fields → Search / Search filters** shows `SearchField`: a search icon on the left and an optional filter button inside the right edge, opening a branded dropdown. Escape and outside clicks dismiss the dropdown; Escape returns focus to the button. Inputs indicate focus through their existing border/label colours, without an additional outer ring. Button keyboard-focus indicators remain intact.

Import `SearchField` from the shared UI package and supply controlled filter controls in its `filters` slot. Translate `label`, `filterLabel` and `closeFiltersLabel`. The owning feature supplies filter values, option counts and the actual client/server query; the search control does not invent filtering rules or claim that loaded counts cover all server records. Filters render in a portal, so use controlled values/callbacks rather than relying on an outer form submission. `DataTable` exposes the same dropdown through `searchFilters`; its Columns control remains separate at the trailing edge. See **DataTables → Tables → Search filter dropdown**.

Build and test the catalogue from `web`:

```bash
corepack pnpm storybook:build
corepack pnpm storybook:test
```

Interaction and accessibility tests run in headless Chromium. On a fresh machine install that browser first:

```bash
corepack pnpm --filter @trykatch/web exec playwright install chromium
```

## Add a story

**Application UI → Member invitations → Default role forbidden** demonstrates a manager without role-read access submitting the existing default-Member request and receiving an authority denial with the recipient preserved. The seeded Member role grants `roles.read`, so this caller cannot assign it. No restricted role catalog is fetched, and no successful invitation is implied for this permission set.

Colocate `*.stories.tsx` with shared package source, host source or a module's `Web/src` source (existing module root-level stories are also discovered). Export a default metadata object and named stories using `Meta` and `StoryObj` from `@storybook/react-vite`. Prefer importing the real component over recreating its markup.

Each package containing stories declares `@storybook/react-vite`, `storybook` and, for network fixtures, `msw` as development dependencies. Keep fixtures in story-only files; never import them into application entrypoints. The worker lives only in `web/apps/storybook/public`, not the production app's public directory.

API-dependent stories declare explicit `parameters.msw.handlers.api` handlers using MSW. Authentication/session, antiforgery and empty permission defaults are supplied centrally; replace the appropriate named group when a story needs different permissions. Unmatched `/api` or `/connect` requests fail rather than reaching a backend. Every story gets a fresh query cache, memory router and account-security completion state. These fake permissions do not change production authorization.

Use `play` functions and `storybook/test` for user interactions. Cover loading, empty, validation, permission and conflict states where supported by the owning UI. Do not describe unimplemented table resizing or reordering. Storybook supplements backend security and end-to-end checks.
