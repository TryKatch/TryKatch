# Module authoring direction

The generated template's operational module contract and reference layout live in [`templates/flatpack/docs/modules.md`](../templates/flatpack/docs/modules.md). The canonical solution must prove a module seam before Flatpack publishes a module marketplace or promises hot installation.

The delivery order is:

1. Explicit backend and React module catalogs with dependency validation.
2. Projects as the first tier-spanning reference module. **Implemented.**
3. A package-shaped `GettingStarted` module with a manifest, secured API contribution, owned permission, React route, navigation, and cross-module extension. **Implemented.**
4. Module-owned permissions, RLS, audit/outbox behavior, routes, and navigation. **Implemented for Projects; the reference package proves the non-data path.**
5. Named web extension slots beyond routes and navigation. **Implemented for UI slots; typed table/form/command contracts remain.**
6. A CLI-managed NuGet + npm package pair with add, disable, doctor, upgrade, eject, remove, and separate purge-data workflows.
7. An optional provider-neutral assistant module with permission-filtered tools and approval-gated mutations.

Until lifecycle tooling and its release gates exist, external module installation is an architectural capability under development rather than a production support promise.
