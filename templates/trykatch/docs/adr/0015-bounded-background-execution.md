# ADR 0015: Bounded background organization execution

Status: Accepted for the parity implementation architecture. Runtime implementation and qualification remain required.
Date: 2026-10-23.

## Context

Organization HTTP work currently resolves an authenticated actor's active membership in an actor-scoped transaction, then enlists application data and sets PostgreSQL transaction-local context. A background DI scope alone establishes none of this. Existing audit intents also require a real nonempty actor identity. Reusing captured cookies/permissions, fabricating Owner membership or promoting a platform operator to a universal tenant actor would weaken the kernel.

The parity plan needs three different kinds of work: a user's deferred command, a required effect of an already committed event, and a periodic system operation. They cannot share the same authorization rule. A user losing membership should stop their unexecuted command; it should not erase accounting/notification intent already committed under valid authority.

## Decision

Use explicit build-time operation/grant registration and one host-owned organization executor, with the exact shapes in the [contract ledger](../specs/feature-parity-contracts.md#4-execution-contract--e1e2).

- **UserCommand:** preserve origin identity as provenance, re-resolve current active membership, required permissions and resource authority at execution. Revoked access is a denied terminal outcome. Session/delegation limits, where applicable, are rechecked rather than copied.
- **CommittedEvent:** run under a finite registered event-effect grant and named host system audit principal. Preserve the origin user separately. Revalidate current organization state, target resources and recipient disclosure, but do not substitute the origin user's later permission state for the committed event's authority.
- **ScheduledSystem:** run only a registered finite operation/resource grant under a named system principal. No wildcard operation, fake user membership, generic support impersonation or RLS bypass is allowed.

The host loads durable work by ID, verifies expected registration/version/hash, and selects the trusted effective actor. It begins the actor-scoped control-plane transaction and sets `app.organization_id`/`app.actor_id` before resolving current membership/access. Within that same transaction, it enlists application data and locks the trusted work identity to inspect only the host completion/inbox marker under forced RLS. A matching organization, work/event identity, actor, operation/version and payload hash may acknowledge an already committed effect before mutable authorization checks; it never invokes a handler or discloses business data. Without a matching marker, it validates the current user/system authority and supported placement before invoking the registered handler, then commits required audit/inbox/outgoing intent with the effect. Coordinator claims use separate credentials; business execution uses the organization data boundary. No provider retry surrounds an external effect.

System audit actor IDs come from validated grant registration and resolve to a safe named system label. They are attribution identities, not bearer authorization tokens or fabricated Identity users. Add an explicit system-principal branch to host context/permission evaluation; never fill `MembershipId` with a sentinel merely to satisfy a membership-required use case. Existing user-only management use cases remain user-only unless a separate narrow host operation is specified.

Disabled modules or inactive organizations pause compatible work without deleting it. Unknown operation/schema/grant versions or malformed scope are terminal compatibility failures; missing configuration/credential/schema faults fail readiness as classified by existing worker principles. User access denial is not an infinite retry loop. Cancellation before commit rolls back and leaves work recoverable.

Every external disclosure is independently current-authorized. In particular, source/channel access is checked on queued-email retries and realtime delivery; system event authority does not itself disclose private payloads. Already transmitted bytes/email cannot be recalled.

## Alternatives

| Alternative | Why not selected |
| --- | --- |
| Capture the originating HTTP context/cookie or permission set | Expired/revoked authority could run later; it is not trusted durable provenance. |
| Use a platform administrator or fabricated Owner membership for all jobs | Conflates catalogs and defeats bounded authority; platform access does not imply tenant membership. |
| Reauthorize every committed effect as its original user | A later revocation could suppress already committed bookkeeping and make event processing nondeterministic. |
| Give workers broad database ownership/BYPASSRLS | Violates the existing least-privilege isolation model. |

## Consequences and proof

E1/E2 must add versioned metadata, a validated principal model, narrow system grants and actor attribution support before any production consumer uses them. This ADR does not grant any database privilege, approve D4 support delegation, or assert existing runtime support.

Required cases include membership resolution inside the actor-scoped transaction, revoked user command, committed effect after origin revocation, denied recipient, forged scope/grant, wrong organization, pooled-connection context reset and disabled/inactive pause. Use real PostgreSQL/production composition for RLS claims; see the [acceptance matrix](../specs/feature-parity-acceptance.md) and [Phase 0 evidence](../specs/evidence/feature-parity-phase-zero.md).
