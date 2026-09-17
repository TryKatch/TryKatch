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
