# Upload admission component evidence

This checkpoint qualifies the S5 host upload lifecycle against actual PostgreSQL runtime roles and the real local conditional object adapter. The Documents upload use case, multipart operation UUID, UI retry behavior, closed removal accounting and leased maintenance execution are still required. The tests enable a test-only Documents upload declaration; the production Documents declaration and upload flow have not switched to this service yet.

## Implemented boundaries

- `ModuleUploadAdmission` requires the existing shared ReadCommitted parent transaction and current original user membership/compiled upload-policy permission. A fresh child scope begins/enlists its own transaction, resolves access again and publishes before acquiring the common resource lock. Upload-kind hold, immutable upload operation and closed recovery reference commit together before a private receipt is returned.
- Receipts bind issuer, actual provider transaction and EF owner transaction lifetime, policy, original actor/membership, payload and metadata identity, stable subject/key and actual storage namespace. Changed identities conflict. Expired admission cannot be renewed or finalized. Existing committed retries skip the metadata callback.
- Finalization verifies the current owned provisional object and runs metadata, audit, outbox, exact positive ledger effect and both completion transitions within the parent transaction. Callback/accounting failure leaves its commit guard incomplete, even when the caller handles the original failure. Sequential pre-admitted uploads share the parent's atomic outcome.
- `ModuleUploadFencing` uses fixed empty-permission technical scopes. It commits Fencing under the same nonblocking resource lock, disposes that transaction, conditionally confirms a permanent sentinel, then revalidates and releases the hold in a fresh transaction. Busy finalization, committed state, mismatched identity/namespace and unconfirmed storage never permit release. Current user membership is not required to compensate the original host-issued admission.
- Incomplete declared inventory queues one active inventory reference, including when finite upload admission is unavailable. The actual organization role performs this with INSERT only and no queue read privilege.

## Targeted qualification

All listed successful results have zero skips. Successful runs are reused; counts below do not count earlier failed or overlapping runs twice.

| Evidence | Qualified cases | Behavior covered |
| --- | ---: | --- |
| `upload-admission-composite-green` | 21 PostgreSQL/local-storage cases | Durable child commit across parent rollback; separately changed file SHA and metadata; missing permission/current membership; another actor's retry; finite inventory intent; raw/tracked parent lock refusal; transaction-only identity conflict; missing parent; atomic finalization/completed retry; callback/accounting failure; two-upload success/failure; copied/cross-scope receipts; missing object; technical cancellation; committed/busy protection; foreign object refusal. |
| Successful lifetime cases in `upload-lifetime-quota-regressions` | 3 PostgreSQL/local-storage cases | Earlier receipt rejected in a new transaction of the same scoped service; expired admission refused but still fenceable; a new actual storage destination cannot rebind an existing operation. |
| `upload-capacity-boundaries-green` | 2 PostgreSQL/local-storage cases | Existing durable holds consume the final slot; a confirmed fence frees it without reusing the key; oversized file cannot create a hold or upload operation. |
| `upload-quota-publication-regressions` and `upload-quota-publication-data-regressions` | 48 existing PostgreSQL cases | All selected asynchronous cases from the quota engine, quota boundary and entitlement publication files, including parameterized actor/organization mismatch, snapshot isolation and target/outage refusal. |
| `upload-admission-host-architecture` | 2 checks | Module project graph and explicit composition/scanning rules. |

The cancellation ordering case wraps the real local adapter and uses an independent owner connection to observe durable Fencing with the hold still reserved immediately before conditional replacement. It then verifies the sentinel and released hold. These are observed storage/database effects, not mocked cancellation results.

The parent commit guard was separately qualified at `12848c5`:4 identity/lifetime unit cases,8 PostgreSQL transaction cases and39 directly affected existing organization/cache transaction cases. Its successful evidence remains valid; these runs are not repeated for the later service-only changes.

## Failures investigated

The initial service scaffold returned Unavailable: both initial reservation cases failed. Once reservation was implemented,9 reservation cases passed while4 finalization cases failed against the unavailable finalization scaffold. Implementing finalization made those4 pass. All4 cancellation cases failed against the cancellation scaffold and passed after the fixed fencing lifecycle was implemented.

An existing final-slot test failed in a combined run and again in isolation: concurrent entitlement publication could return `quota_unavailable` before the test reached its intended resource-slot contest. The fixture now publishes both participants before starting that contest. It retains the original `quota_busy`/exhaustion assertions and does not weaken the admission contract. All48 selected existing quota/publication cases then pass. A Name-equality filter initially omitted three parameterized methods; their6 data cases were explicitly selected and qualified afterward.

Compiler errors during development included a static fixture-helper requirement, a missing host context-initializer namespace and duplicate primary-constructor parameter capture. They were corrected before behavioral qualification. No production exception handling was weakened to silence these errors.

## Remaining acceptance

This is component evidence. It does not prove the actual Documents API/UI upload flow, archived/deleted result retry authorization, closed removal/refund policy, leased claims/worker restart, two API replicas with RustFS, packed/renamed generated outputs, request throttling, usage UI, whole-S5 review/CI/merge or the final main-branch release. Those gates remain required by the canonical S5 plan and overall parity roadmap. S3/RustFS adapter evidence from Task4 remains separate; this checkpoint makes no physical purge or power-loss durability claim.
