# TryKatch Tickets Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans task-by-task, using the project's single review workflow.

**Goal:** Deliver a support-ticket reference with real transitions, assignment, comments and organization numbering.
**Architecture:** Optional organization-owned five-layer module; state transitions are domain actions with expected-version concurrency. Notifications is an optional event integration.
**Tech Stack:** Existing module stack, E3 events and S3 mutation idempotency.
**Spec:** [Design](../specs/2026-10-23-feature-parity-design.md); [roadmap](2026-10-23-feature-parity-plan.md).

## Global Constraints

Apply all roadmap constraints. Paths below are relative to `templates/trykatch/`; new names are proposed. No required business module, SLA/escalation/email-to-ticket gateway or initial attachment feature. A blueprint can start the source but is not evidence of numbering, current assignee authority or transactional concurrency.

## Review Focus

- Concurrent creates never allocate the same organization ticket number.
- State transitions and assignment behavior are checked in Domain/Application, not a writable status field.
- Closed tickets reject edits/comments until a valid reopen operation.
- Reporter/assignee IDs belong to the current organization with appropriate authority.
- Optional notifications cannot break ticket commits or create cross-organization disclosure.

## T1: Implement ticket aggregate, workflow, numbering and APIs

**Files:** Create `src/Modules/Tickets/trykatch.module.json` and five Tickets projects; Domain `Ticket.cs`, `TicketComment.cs`, `TicketStatus.cs`, `TicketPriority.cs`; Application `TicketUseCases.cs`, `TicketNumberAllocator.cs`; Infrastructure `TicketsModule.cs`, `TicketsModelContributor.cs`, `TicketNumberStore.cs`, forward migrations; Presentation `TicketsEndpoints.cs`; module unit/architecture tests and host `TicketWorkflowIntegrationTests.cs`.

**Interfaces:** Operation IDs `Tickets_List`, `Tickets_Get`, `Tickets_Create`, `Tickets_Update`, `Tickets_Assign`, `Tickets_Transition`, `Tickets_AddComment`, lifecycle operations. Permissions `tickets.read`, `tickets.create`, sensitive `tickets.manage`, `tickets.assign`. Commands carry expected version; actor/reporter and organization are trusted host context. Events `tickets.created.v1`, `tickets.status-changed.v1`, `tickets.assigned.v1` are safe published contracts.

| Action | From | To / guard |
| --- | --- | --- |
| Start | Open | InProgress |
| Resolve | InProgress | Resolved |
| Close | Resolved | Closed; closing an already Closed ticket is a no-op |
| Reopen | Resolved or Closed | Open; proposed reference behavior clears assignment and records the action |
| Assign | Open or InProgress | InProgress; target must be an active eligible organization member |
| Unassign | InProgress | Open |
| Edit/comment | Open, InProgress or Resolved | No status change; immutable posted comments; Closed rejects with conflict |

- [ ] Record this proposed reference workflow, priority values Low/Medium/High/Critical, read/assignment policy and field constraints in `docs/specs/tickets.md`. Any product-specific deviation updates the state table/tests together.
- [ ] Add `EveryIllegalTransitionReturnsConflict`, `AssignmentRequiresCurrentEligibleMember`, `ClosedTicketCannotEditOrComment`, `StaleVersionCannotOverwriteState`, `ConcurrentCreatesHaveUniqueOrganizationNumbers` and cross-organization identifier tests.
- [ ] Allocate sequential display numbers with a per-organization database constraint/allocator; define them as unique and increasing, not guaranteed gap-free. An archived ticket retains its number. Do not use a process-local counter.
- [ ] Implement transition methods and expected-version checks in the aggregate/use case, append comments with immutable author/time, and atomically persist audit/events. Use idempotency for create/comment actions; natural no-op close is separately defined.
- [ ] Verify module unit/architecture tests, `TicketWorkflowIntegrationTests`, applicable RLS/atomicity/idempotency regressions and common exit.

## T2: Add ticket UI, optional inbox events and browser acceptance

**Files:** Tickets `Web/` package with `TicketsPage.tsx`, `TicketDetailsPage.tsx`, tests/stories/messages; optional Notifications event adapter; `web/apps/web/e2e/tickets.spec.ts`; module docs.

**Interfaces:** Routes `/tickets` and `/tickets/$ticketId`; generated state/action DTOs expose currently allowed actions but the server remains authoritative. Notifications can consume installed ticket contracts without a required dependency in either direction.

- [ ] Cover list/filter/search, create, priority, current-member assignment, transition controls, immutable comment display, conflict reload, archived/closed states and permission denials. Match shared UI and English/French vocabulary.
- [ ] Browser case creates/assigns/resolves/closes a ticket, verifies closed edits/comments fail, reopens it and shows a conflict from a second editor. Confirm B cannot search/read A's ticket and a removed assignee cannot be selected.
- [ ] Test Tickets alone and Tickets+Notifications: the business commit succeeds independently; an optional inbox failure retries its own effect and cannot roll back or duplicate the ticket.
- [ ] Verify focused module Web/stories, named browser scenario, `TicketWorkflowIntegrationTests`, generated client/typecheck/build and common exit. Record evidence in proposed `docs/specs/evidence/parity-tickets.md`.
