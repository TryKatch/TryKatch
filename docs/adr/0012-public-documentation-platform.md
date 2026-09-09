# ADR 0012: Public documentation platform

Status: Accepted  
Date: 2026-09-09

## Context

Trykatch needs public, searchable documentation with a sidebar, table of contents, sequential navigation, code examples, dark mode, and architecture visuals. The source must remain easy to review, reuse, and move without depending on a proprietary hosted editor.

## Decision

Use Astro Starlight as a standalone static site under `docs-site`. Author pages in Markdown or MDX and deploy the generated site to `docs.trykatch.net`. Keep internal research and ADRs in the root `docs` tree. Generate architecture artifacts locally from version-controlled Archify specifications, embed theme-aware images in the public pages, and offer the full interactive HTML diagram as a progressive enhancement.

Do not introduce documentation version snapshots before Trykatch publishes a stable release with an explicit support policy.

## Consequences

- Documentation stays portable and reviewable as text.
- Search requires no external service or API key.
- The documentation site has its own dependency lockfile and deployment boundary.
- Public documentation is not copied into every generated application.
- Architecture changes require regenerating and reviewing both the specification and its delivered artifact.
