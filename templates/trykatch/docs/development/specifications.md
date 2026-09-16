# Feature specifications

Keep feature specifications in `docs/specs/<feature>.md`. Reuse a covering spec; a small localized fix can use the request and a regression test instead. Architectural decisions belong in `docs/adr`, not in every feature spec.

Use the following outline, omitting sections that do not apply:

1. **Outcome and scope:** who needs the capability, observable outcome, included behavior, explicit exclusions.
2. **Evidence and decisions:** supplied requirements, source links, assumptions, unresolved business decisions. Mark blocked behavior without blocking unrelated work.
3. **Ownership and vocabulary:** owning module, organization/platform/global boundary, domain terms and relationships. Reuse terms from existing code and ADRs.
4. **Business rules:** field constraints, lifecycle, state/action/from/to/guard table where there is a workflow, conflicting writes, retries, and recovery behavior.
5. **Permissions:** operation, permission ID, intended default grants, and expected denial behavior. State which layer enforces each rule.
6. **Implementation:** existing seams, new files/components, generator or blueprint fit, necessary custom logic, API/event compatibility, migration/data-retention approach, optional UI and translations.
7. **Acceptance cases:** concrete Given/When/Then examples with the appropriate domain, API, PostgreSQL, or browser test. Include negative paths where they matter.
8. **Verification record:** each criterion's status and evidence; distinguish implemented behavior from checks actually run.

For rental reservations, for example, “reject overlapping reservations for the same equipment” needs a defined interval rule and a transaction/concurrency strategy. A generated CRUD form plus a `Reserved` status does not satisfy that requirement. State unresolved interval or cancellation policy explicitly instead of inventing it.

Write only decisions needed for the requested feature. A specification is not a new authorization boundary: proceed when implementation is already requested and requirements are sufficient; seek a decision only when behavior depends on it.
