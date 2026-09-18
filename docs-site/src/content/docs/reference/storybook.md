---
title: UI catalogue (Storybook)
description: Run and test Trykatch's branded React catalogue without a backend.
---

From your generated application's root:

```bash
cd web
corepack pnpm install --frozen-lockfile
corepack pnpm storybook
```

Open `http://localhost:6006`. No API, Docker or Aspire is required. Backend-only applications do not include Storybook. Use the toolbar for light/dark themes, English/French and viewport sizes.

From `web`, build and run interaction and accessibility checks:

```bash
corepack pnpm --filter @trykatch/web exec playwright install chromium
corepack pnpm storybook:build
corepack pnpm storybook:test
```

In a renamed application, replace `@trykatch/web` with its web package name from `web/apps/web/package.json`.

Stories are discovered automatically in shared package source, host source and module `Web/src` directories. Add `*.stories.tsx` next to the owning component. Use the actual component, explicit MSW API fixtures, and `play` interaction tests. Unexpected application API requests fail instead of contacting a server. Keep fixtures and Storybook's service worker out of production entrypoints and public directories.

Native form stories document existing HTML controls; they do not introduce a separate form framework. Table stories demonstrate supported pagination, sorting, visibility and density—not resizing or reordering. Storybook does not replace backend authorization, PostgreSQL isolation or end-to-end tests.

Start with **Welcome → Catalogue guide**. Find required-field validation, correction, saving and preserved-input server errors in **Forms → Validation**. Real create and upload forms live under **Module UI → Projects / Documents**.

**Application UI → Member invitations** demonstrates role selection, loading, unavailable roles and retry. A failed roles request is not an empty catalogue and does not grant a fallback role. Invitation, profile, tenant-management and account-security text fields reuse floating controls; checkboxes, radios, file pickers and native selects retain appropriate labels. **Application UI → Organization settings** covers organization-owned subscriptions, write-only fake keys, replacement/removal, saved connection tests, activation, read-only access, preserved-input errors and conflict reload. Provider calls are mocked, not paid-provider UAT.

The organization settings provider uses the shared branded `FloatingSelect` dropdown. **Forms → Floating dropdown** checks keyboard selection, Escape/focus recovery and error/disabled states; **Organization settings → Compact floating controls** protects 38 px inputs, transparent labels and a single focus outline in the real host layout. New and edited text-like fields must reuse the shared floating controls rather than hand-written label CSS.

**Forms → Floating fields** demonstrates shared floating-label inputs and textareas, including empty, filled, invalid, disabled, read-only, numeric and date fields. Role, project and document metadata forms use these controls. Labels remain accessible and visible above entered values; file pickers and selects retain native visible labels.

Labels float onto the top border on focus, stay there when filled, and return inside after clearing and blur. A decorative fieldset/legend opens a real notch around the transparent label, without a background patch or a second focus ring. The notch closes when the empty field loses focus. Table, collection, audit and permission searches use the same behavior and retain their icons. The role editor scrolls its form body separately from the persistent action footer; see **Forms → Validation → Constrained height / Narrow saving**.

Floating fields use Trykatch's compact heights: 38 px standard inputs (just 2 px above the 36 px baseline), unchanged 38 px role inputs and 34 px search. Textareas respect native row counts and resizing; button sizing is unchanged.

Generic form styles must not override labels owned by `.floating-control` or put their required marker on another line. **Forms → Floating fields → Host form contexts** verifies shared controls inside authentication, dialog, profile and role forms. **Application UI → Authentication → Sign in / French sign in / Dark sign in** exercises the real sign-in page through focus, typing, blur and clearing. Keep these integration checks when changing form CSS; the same styles ship in generated React projects.

**Forms → Floating fields → Search / Search filters** demonstrates the shared `SearchField`, with a search icon on the left and optional filter button inside the right edge. Its branded dropdown supports Escape, outside dismissal and focus restoration. Inputs use one border with focus colours, not an extra outer ring. Features supply controlled filters through `filters`, their translated labels and accurate counts/query behavior; portal controls must not depend on parent-form submission. `DataTable.searchFilters` exposes the same interaction while Columns remains separate. See **DataTables → Tables → Search filter dropdown**.

Organization role, project and document metadata forms use Zod 4.5.4. FluentValidation 12.1.1 validates their corresponding backend commands independently. Other forms may still use native/custom validation. Shipped AI skills require consulting Storybook before frontend work and maintaining relevant component and validation stories.
