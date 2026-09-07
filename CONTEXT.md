# Domain language

## Organization

The customer-visible account and data-isolation scope. An organization has members, roles, invitations, and organization-owned records.

## User

A globally unique human identity. A user may belong to multiple organizations.

## Membership

The association granting a user access to one organization.

## Role

An organization-owned named bundle of permissions. Owner, Admin, Member, and Viewer are seeded roles, not hard-coded authorization branches.

## Permission

A stable, code-defined capability that can be assigned to a role.

## Platform administrator

A global operator who can create and manage organizations. Platform administration is not an organization role.

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
