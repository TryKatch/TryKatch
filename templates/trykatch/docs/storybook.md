# UI catalogue

Run from the generated application's root, the directory containing its solution and `trykatch.modules.json`:

```bash
cd web
corepack pnpm install --frozen-lockfile
corepack pnpm storybook
```

Open <http://localhost:6006>. Storybook uses fixtures: **do not start the API, Docker or Aspire**. This is a UI development catalogue, not a running business application. Backend-only projects do not include it.

Use the toolbar to switch between light/dark, English/French and phone/tablet/desktop viewports. The catalogue loads the real application stylesheet and its CSS variables. Native input stories are reusable examples, not an additional form library.

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

Colocate `*.stories.tsx` with shared package source, host source or a module's `Web/src` source (existing module root-level stories are also discovered). Export a default metadata object and named stories using `Meta` and `StoryObj` from `@storybook/react-vite`. Prefer importing the real component over recreating its markup.

Each package containing stories declares `@storybook/react-vite`, `storybook` and, for network fixtures, `msw` as development dependencies. Keep fixtures in story-only files; never import them into application entrypoints. The worker lives only in `web/apps/storybook/public`, not the production app's public directory.

API-dependent stories declare explicit `parameters.msw.handlers.api` handlers using MSW. Authentication/session, antiforgery and empty permission defaults are supplied centrally; replace the appropriate named group when a story needs different permissions. Unmatched `/api` or `/connect` requests fail rather than reaching a backend. Every story gets a fresh query cache, memory router and account-security completion state. These fake permissions do not change production authorization.

Use `play` functions and `storybook/test` for user interactions. Cover loading, empty, validation, permission and conflict states where supported by the owning UI. Do not describe unimplemented table resizing or reordering. Storybook supplements backend security and end-to-end checks.
