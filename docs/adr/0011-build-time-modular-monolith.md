# 0011: Compose full-stack modules at build time

- Status: Accepted
- Date: 2026-09-08

Flatpack uses an explicit, contract-driven modular monolith. Modules are selected at installation or build time, validated for identity and dependency conflicts, and registered through a small `IFlatpackModule` interface. React uses the same explicit model for routes and navigation. This preserves one-process deployment and Clean Architecture while letting future packages add capabilities without editing central feature lists. Required dependencies must exist; optional dependencies participate in ordering only when installed. Arbitrary runtime assembly loading was rejected because it complicates trimming and dependency compatibility, and loading untrusted code in-process is not a security boundary. Organization resolution, RBAC, RLS, auditing, and module validation remain a non-replaceable security kernel.

The design is informed by [the OpenMercato modularity research](../research/openmercato-modularity-research.md), but Flatpack uses a clean-room .NET and React implementation with explicit registries rather than convention-based runtime scanning.
