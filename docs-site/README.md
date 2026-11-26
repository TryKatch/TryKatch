# Trykatch documentation site

The public documentation is a standalone Astro Starlight site. Product documentation remains Markdown or MDX under `src/content/docs`; Starlight supplies navigation, previous/next links, table of contents, dark mode, and local Pagefind search.

```bash
pnpm install --frozen-lockfile
pnpm build
pnpm dev
```

Architecture sources live in `../docs/diagrams`. Generated visual assets are copied into `public/diagrams` after Archify validation and delivery.

Images use Astro's passthrough service because the site currently serves SVG logos and static diagram assets without image transformations. Astro image components can still render images, but this service does not resize, optimize, or convert them; revisit the service choice if the site adopts processed images.

English content uses the root routes. French translations mirror every page under `src/content/docs/fr/` and are published below `/fr/`.
