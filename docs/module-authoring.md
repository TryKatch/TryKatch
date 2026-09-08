# Module authoring direction

The generated template's operational module contract and reference layout live in [`templates/flatpack/docs/modules.md`](../templates/flatpack/docs/modules.md). The canonical solution must prove a module seam before Flatpack publishes a module marketplace or promises hot installation.

The delivery order is:

1. Explicit backend and React module catalogs with dependency validation.
2. Projects as the first tier-spanning reference module. **Implemented.**
3. A package-shaped `GettingStarted` module with a manifest, secured API contribution, owned permission, React route, navigation, and cross-module extension. **Implemented.**
4. Module-owned permissions, RLS, audit/outbox behavior, routes, and navigation. **Implemented for Projects; the reference package proves the non-data path.**
5. Named web extension slots beyond routes and navigation. **Implemented for UI slots; typed table/form/command contracts remain.**
6. A CLI-managed NuGet + npm package pair with add, disable, doctor, upgrade, eject, remove, and separate purge-data workflows. **The authoritative full-stack catalog plus list, doctor, deterministic generation, and dependency-safe enable/disable are implemented; acquisition, upgrade, eject, unregister, and purge remain.**
7. A provider-neutral assistant contract generated from explicitly allowlisted module operations. **Implemented for strict read-only tools; a runtime provider adapter, permission-filtered discovery, approval UI, and mutation execution remain.**

CI now enforces catalog validity and backend/web registry parity, including in generated template permutations. Until package acquisition, upgrade, eject, unregister, migration ownership, provenance, and rollback gates exist, external third-party module installation remains an architectural capability under development rather than a production support promise.
