# 0011: Compose full-stack modules at build time

- Status: Accepted
- Date: 2026-09-08

FlatpackApp uses an explicit, contract-driven modular monolith. Modules are selected at installation or build time, validated for identity and dependency conflicts, and registered through a small `IFlatpackModule` interface. Enabled modules own controller activation, EF model contributions, permission definitions and setup defaults. React uses the same explicit model for routes, navigation, named extension points, permission-gated contributions, and application-owned keyed overrides. This preserves one-process deployment and Clean Architecture while letting future packages add capabilities without editing central feature lists. Required dependencies must exist; optional dependencies participate in ordering only when installed. Arbitrary runtime assembly loading was rejected because it complicates trimming and dependency compatibility, and loading untrusted code in-process is not a security boundary. Organization resolution, RBAC, RLS, antiforgery, auditing, and module validation remain a non-replaceable security kernel.
