# Documentation platform selection

Status: Accepted for the Trykatch public documentation site  
Reviewed: 2026-09-09

## Decision

Use **Astro Starlight** for `docs.trykatch.net`. Keep public content in Markdown or MDX, keep architectural decisions and research as repository Markdown, and commit locally generated architecture visuals beside their Archify specifications.

Starlight is the best fit because it is a documentation-first static site in the same TypeScript ecosystem as the Trykatch web workspace. It supplies navigation, SEO, accessible typography, dark mode, code highlighting, internationalization, and Pagefind full-text search without a hosted search dependency. It supports both Markdown and MDX, while Astro still permits React components when a genuinely interactive explanation is needed. [Starlight overview](https://starlight.astro.build/), [pages and MDX](https://starlight.astro.build/guides/pages/), [site search](https://starlight.astro.build/guides/site-search/)

## Alternatives considered

### Docusaurus

Docusaurus is a strong React/MDX documentation platform with localization, search integration, and documentation versioning. It remains the preferred fallback if Trykatch later needs deeply React-specific documentation components or several simultaneously supported documentation versions. Its own guidance warns that versioning adds contributor and maintenance complexity and is often unnecessary; Trykatch does not need that cost during preview development. [Docusaurus documentation](https://docusaurus.io/docs), [versioning guidance](https://docusaurus.io/docs/versioning)

### VitePress

VitePress is fast, clean, and includes local MiniSearch-powered full-text search. It is Vue-based, so adopting it would introduce a second UI component ecosystem without a corresponding Trykatch requirement. [VitePress guide](https://vitepress.dev/guide/getting-started), [local search](https://vitepress.dev/reference/default-theme-search)

### Material for MkDocs

Material for MkDocs is mature, Markdown-first, responsive, and includes built-in search. It would add a Python documentation toolchain to a .NET and TypeScript repository, and its maintainers state that the 9.7 release line is the last to receive new features. It remains suitable for Python-centered teams, but is not the best long-lived fit here. [Material for MkDocs](https://squidfunk.github.io/mkdocs-material/), [changelog](https://squidfunk.github.io/mkdocs-material/changelog/)

## Operational consequences

- Documentation builds to static files and can be hosted without a documentation SaaS subscription.
- Pagefind keeps search local to the built site; no external indexing key is required.
- Content reviews remain ordinary pull-request diffs.
- The product landing page links to the stable `https://docs.trykatch.net` origin.
- Architecture visuals are generated from versioned JSON and delivered as interactive HTML plus theme-aware images.
