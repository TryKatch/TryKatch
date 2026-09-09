# Trykatch documentation site

The public documentation is a standalone Astro Starlight site. Product documentation remains Markdown or MDX under `src/content/docs`; Starlight supplies navigation, previous/next links, table of contents, dark mode, and local Pagefind search.

```bash
pnpm install --frozen-lockfile
pnpm build
pnpm dev
```

Architecture sources live in `../docs/diagrams`. Generated visual assets are copied into `public/diagrams` after Archify validation and delivery.

English content uses the root routes. French translations mirror every page under `src/content/docs/fr/` and are published below `/fr/`.
