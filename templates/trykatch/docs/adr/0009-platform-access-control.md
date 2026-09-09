# ADR 0009: Separate platform access from tenant authorization

## Status

Accepted.

## Context

A global identity can belong to many tenant workspaces and may also need limited access to platform administration. Treating every platform user as a single boolean administrator prevents least privilege and risks leaking tenant authorization into platform operations.

## Decision

Platform access uses ASP.NET Core Identity roles stored in the `identity` schema. Trykatch seeds three immutable roles:

- **Administrator**: full platform control, including access and authentication policy.
- **Operator**: tenant and invitation operations without privileged access management.
- **Auditor**: read-only platform visibility.

Each role expands to stable `platform.resource.action` permission claims. Controllers authorize permissions, not role names. Platform roles never imply organization membership or bypass PostgreSQL RLS.

Administrators may add organization-specific platform roles. Custom roles are persisted as Identity roles and claims, but every grant must come from the code-defined platform permission catalog published by the API. The React permission editor consumes that catalog, so a module can introduce a permission without duplicating it in the web application. Built-in roles remain protected, assigned custom roles cannot be deleted, and role updates invalidate affected sessions.

New platform identities receive a 24-hour Identity activation token and choose their own password. Suspending, changing, or removing platform access rotates the security stamp. An administrator cannot demote, suspend, or remove themselves, and the final active administrator is protected by a serialized PostgreSQL transaction and advisory lock.

Removing platform access removes only platform role assignments. It does not delete the global identity or change tenant memberships.

Creating a tenant does not grant its platform operator access to customer data. Provisioning creates the workspace and a seven-day invitation for its first Owner. The invited identity creates an account or signs in, accepts the invitation, and receives the protected workspace-context cookie used by organization middleware.

## Consequences

The React platform Users surface can discover roles and permissions from the backend without duplicating authorization rules. Additional roles can be introduced in the platform-access module while controllers and tables continue to consume the same interface. Privileged changes remain centralized and race-safe.
