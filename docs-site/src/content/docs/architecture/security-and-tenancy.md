---
title: Security and organization isolation
description: Learn how Trykatch resolves organizations and enforces authorization and PostgreSQL RLS.
---

Trykatch uses **Organization** as the customer-facing term. Tenant describes the underlying isolation mechanism only.

## Request-derived context

Organization-neutral URLs keep technical tenancy out of the route. After authentication, middleware resolves the active organization from server-protected session context or another trusted host integration, then revalidates membership before any scoped work begins.

## Defense in depth

Every organization-scoped request passes through four controls:

1. authentication identifies the global user;
2. organization resolution establishes the active organization;
3. permission handlers authorize the requested capability;
4. the database transaction sets `app.organization_id` and `app.actor_id`, after which forced PostgreSQL RLS filters scoped tables.

The runtime database role cannot own tables or bypass RLS. A separate migrator credential owns schema changes.

Modules that persist data must also satisfy Trykatch's executable [module data-isolation contract](/architecture/module-data-isolation/). The contract validates EF Core models, PostgreSQL policies, runtime roles, and cross-organization behavior before release.

## Platform access is separate

Platform administrators and support operators use platform roles and permissions. Organization roles never grant platform authority, and platform permission names are validated against their own code-defined catalog.

:::note
RLS is a final isolation boundary, not a replacement for application authorization. Trykatch requires both.
:::
