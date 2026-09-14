# Module authoring direction

The generated template's operational module contract and reference layout live in [`templates/trykatch/docs/modules.md`](../templates/trykatch/docs/modules.md). The canonical solution must prove a module seam before Trykatch publishes a module marketplace or promises hot installation.

The delivery order is:

1. Explicit backend and React module catalogs with dependency validation.
2. Projects as the first tier-spanning reference module. **Implemented.**
3. A package-shaped `Federation` module with a manifest, secured API contribution, owned permissions, React routes, navigation, and module migrations. **Implemented.**
4. Module-owned permissions, RLS, audit/outbox behavior, routes, and navigation. **Implemented for Projects and Documents; generated organization CRUD modules receive the same mandatory contract.**
5. Named web extension slots beyond routes and navigation. **Implemented for UI slots; typed table/form/command contracts remain.**
6. A CLI-managed NuGet + npm package pair with create, add, disable, doctor, upgrade, eject, remove, and separate purge-data workflows. **The atomic backend/full-stack source generator, authoritative catalog, list, doctor, deterministic generation, package acquisition/upgrade/eject/unregister, and dependency-safe enable/disable are implemented; permanent purge remains deliberately separate.**
7. A provider-neutral assistant contract generated from explicitly allowlisted module operations. **Implemented for strict read-only tools; a runtime provider adapter, permission-filtered discovery, approval UI, and mutation execution remain.**

CI enforces catalog validity, backend/web registry parity, deterministic generated modules, generated-module build/tests, and organization isolation against PostgreSQL. External third-party module installation remains an architectural capability under development rather than a promise of safe hot-loading of arbitrary code.
