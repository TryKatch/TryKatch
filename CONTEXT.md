# Domain language

## Organization

The customer-visible account and data-isolation scope. An organization has members, roles, invitations, and organization-owned records.

## User

A globally unique human identity. A user may belong to multiple organizations.

## Membership

The association granting a user access to one organization.

## Organization role

An organization-owned named bundle of organization permissions. Owner, Admin, Member, and Viewer are protected seeded roles, not hard-coded authorization branches. Custom organization roles can be created, described, edited, archived, restored, and disposed under the recoverable lifecycle policy.

## Platform role

A global bundle of platform permissions for operators who manage the product itself. Administrator, Operator, and Auditor are protected built-in roles; custom platform roles support CRUD. A platform role never grants organization membership or access to organization-owned data.

## Role assignment

The association between a person and a role. An organization membership can hold multiple organization roles and receives the union of their permissions. A platform operator has one platform role so the global administrative boundary remains explicit and reviewable.

## Permission

A stable, code-defined capability that can be assigned to a role.

## Flatpack module

An install-time, full-stack business capability with a stable identity and explicit dependencies. A module may contribute domain behavior, permissions, data, endpoints, background work, web routes, and assistant tools without weakening the security kernel.

## Extension

A typed contribution from one Flatpack module to a named host seam. Extensions add behavior without modifying another module's private implementation.

## Security kernel

The non-replaceable organization resolution, authentication, authorization, PostgreSQL RLS, auditing, and module-validation rules that every Flatpack module must obey.

## Platform administrator

A global operator with the protected Administrator platform role. Platform administration is not an organization role, and role managers cannot create, edit, delete, or assign grants beyond their own effective platform permissions.

## Project

The reference organization-owned aggregate used to demonstrate isolation, authorization, auditing, and CRUD.

## Archive

A durable, reversible inactive state for a record that still has business or historical value. Projects, memberships, and custom roles can be archived. Invitations are revoked or deleted instead of archived.

## Restore

The lifecycle transition that returns an archived or pending-deletion record to active use. Restoration is permission-checked and audited.

## Pending deletion

A recoverable disposal request created by Delete. It requires an accountable human reason and removes the record from active use. Projects, memberships, and custom roles must be archived before entering Pending deletion; invitations can be deleted directly.

## Permanent deletion

An irreversible physical removal performed only by an explicit retention and disposal policy. It cannot be initiated from the normal web application.

## Audit event

An immutable, organization-scoped snapshot of a security-relevant action. Audit events cannot be edited, archived, restored, or deleted through application CRUD.
