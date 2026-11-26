# Confirmed logical usage removal evidence

The S5 host now implements `IOrganizationUsageLedger` through the same scoped `OrganizationQuotaService` that owns quota reads and transaction-only holds. The public method never chooses an organization, actor or arbitrary handler from input and never commits the caller's transaction. Documents has no removal policy or purge endpoint; its archive, restore and deletion-request transitions retain their charges.

## Implemented boundary

The service requires the actual shared ReadCommitted organization transaction, fresh original membership or a compiled owning system quota grant, current active/shared placement, a declared installed resource, and its exact registered removal-policy binding. Publication precedes the common nonblocking resource lock. The module policy performs read-only confirmation of durable permanent-removal evidence and its business authorization. The host rechecks authority and provider/EF-owner transaction lifetime after confirmation.

A debit references an existing positive effect and the same source ID. Exact replay matches actor, source and units and still requires current confirmation. Different identities conflict; unknown/negative sources, invalid requests, unconfirmed removal or insufficient remaining original units deny. All linked debits together are bounded by that original positive charge, including partial refunds and Int64 boundaries. Corrupt or unavailable history fails closed. One acknowledged INSERT is required before returning Recorded; failed persistence requires caller rollback. Allowance mode changes do not prohibit a verified refund of retained usage.

## Qualified component checks

All successful cases below have zero skips. Overlapping earlier runs are not counted again.

| Evidence | Cases | Scope |
| --- | ---: | --- |
| `usage-removal-final-pg` | 18 new PostgreSQL cases | Durable module-owned marker confirmation; caller-local debit versus independent-connection visibility and rollback; committed exact retry; changed source/units/actor conflicts; active/archived/absent-policy refusal; negative/missing source and invalid UUIDs/units; split refunds and over-refund denial; current membership/organization/permission and absent-owner refusal; competing callers and strict quota-busy loser; confirmation failure/caller cancellation; original transaction and GUC frame; membership revoked during confirmation; over-limit Int64 maximum; changed transaction lifetime; suppressed INSERT acknowledgement. |
| `usage-removal-final-pg` plus `usage-removal-parameterized-pg` | 52 existing PostgreSQL cases | Directly affected quota engine, boundary, system-grant and entitlement publication behavior after the shared service gains its required workflow-registry dependency. |
| `usage-removal-events-policy-green`, included again in `usage-removal-final-pg` | 2 new PostgreSQL cases | Real outbox fanout and committed-event processor; injected post-debit failure rolls back accounting/inbox; retry under a compiled system grant after source membership revocation; second actual delivery reuses the logical effect; missing explicit quota grant cannot refund. |
| `usage-removal-authority-documents-units` | 25 checks | Shared system-grant validation, quota catalog provenance and directly affected host Documents fixtures, including no raw deletion before/after rollback compensation. |
| `usage-removal-host-architecture` | 2 checks | Module project graph and explicit composition without assembly scanning. |

The final selection artifact lists 69 distinct test methods from the two new removal files and four affected quota/publication files. The first exact-name selection executes 66 cases and reveals three parameterized methods excluded by that filter. A focused fully qualified-name selection runs those six remaining rows. The final evidence therefore covers 72 unique cases, including all expected methods; overlapping runs are not added to the total. The test resource uses the installed Probe module's actual scoped PostgreSQL record, positive usage effect and exact permanent-removal marker. A plain boolean confirmation stub is not used as proof of removal.

## Failures investigated

The first test build exposed a missing application namespace in the fixture. Initial runtime failures confirmed the missing service registration; the missing-parent test was strengthened so a DI failure could not satisfy its expected boundary exception. Once the service scaffold was registered, the isolated provider also needed the Probe module's existing execution behavior. With that fixture corrected, 14 semantic cases failed against the Unavailable scaffold and two existing scaffold behaviors passed. The implementation made all 16 pass.

Two further real-database boundary cases exercise transaction replacement during confirmation and an owner-installed trigger that suppresses only the selected ledger INSERT. Transaction replacement is refused by the exact original-parent check. The suppressed INSERT initially returned Recorded incorrectly; requiring one affected row corrects that failure. The combined 70-case run is green after that correction. Fixture schema/role rules, required business authorization and production exception handling were not weakened to obtain these results.

The first real event run stalled during dependency resolution before outbox fanout: the consumer injected the ledger, whose shared quota service required the operation registry still constructing that consumer. The stopped run is not a pass. Quota system authority now resolves only the installed catalog's exact descriptor/grant metadata, reusing the same provenance validator without constructing module handlers. Both actual event cases pass after this correction. A test-only attempt to use a new resource-policy key correctly failed undeclared-resource validation; the final fixture uses the existing declared Probe resource and dispatches its specialized policy by the installed operation key.

The host-unit build also exposed three remaining old Documents constructor calls. Those test-only fixtures now supply the current conditional-storage and admission ports; failure and rollback assertions expect host fencing after proven rollback and zero raw writes/deletes. The 25 selected host units pass. Their two legacy raw-removal test names were updated afterward to describe fencing; assertions and behavior are unchanged.

## Remaining acceptance

These checks qualify service transactions and real committed-event retry/delivery. Leased maintenance, request throttling, safe usage UI/initializing-state inspection, packed/renamed generated products, two-replica RustFS/restart/absent-module journeys, the single whole-S5 review, final-head CI/merge and the overall main release remain required. No physical purge, retention policy or commercial billing policy is introduced here.
