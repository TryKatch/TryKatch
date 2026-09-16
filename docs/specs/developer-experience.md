# Developer experience improvements

## Scope

Improve generated modules, local onboarding, and typed frontend contributions. Existing applications are not silently upgraded. Generated source remains owned by the application; registries and API clients remain generated.

## Generated modules

- Add a `/page` read contract alongside the existing list contract. Page size defaults to 25 and is capped at 100; page numbers and search length are validated. Return items and `hasMore`, without an expensive total count.
- Filter lifecycle and searchable string fields in SQL, order by creation date with an ID tie-breaker, then apply offset and limit. Never disable organization query filters or PostgreSQL RLS.
- Generated screens use server search, ordering and page navigation. The legacy array endpoint remains available for compatibility, including the existing archive surface; it is not the scalable read interface.
- Basic CRUD gets the same expected-version protection as business blueprints. Reject absent versions at request binding (400) and stale versions with a structured 409. Rotate versions on changes and use an EF concurrency token to catch racing writes. Refresh is explicit and preserves unsaved editor input.

## Local onboarding

Expose application `doctor`, `setup`, and `status` commands. Doctor is read-only and reports concrete toolchain/workspace problems. Setup restores local dependencies without changing secrets or migrating a shared database. Status distinguishes configuration from live health. Backend-only applications must not require Node or pnpm.

## Typed contributions

Add a typed contribution seam for module-owned table columns and row actions, with stable IDs, deterministic ordering, duplicate detection, and permission filtering. Keep existing UI slots compatible. Extensions are presentation customization, never server authorization.

## Verification

Exercise CLI diagnostics with fake process results; exercise contributions through their public interface; scaffold real CRUD and blueprint applications, compile generated backend and frontend, and run generated tests. Test stale versions, page bounds, SQL pagination and organization isolation. No automatic publishing, merging, or release is included.
