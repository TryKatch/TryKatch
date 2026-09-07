# ADR 0008: Request-derived organization context

- Status: Accepted
- Date: 2026-09-07

## Decision

Organization identity is request context, not route data. Browser routes and scoped API routes are tenant-neutral. After sign-in or workspace selection, the server issues an encrypted HttpOnly workspace cookie. `OrganizationScopeMiddleware` reads that protected context, revalidates the authenticated actor's active membership, and initializes one immutable request-scoped context. Browser code never receives or manufactures a tenant identifier for routine requests. External OpenIddict clients may instead carry a trusted `organization_id` claim.

The resolved organization and actor identifiers are applied transaction-locally to PostgreSQL before scoped queries execute. Application authorization and PostgreSQL RLS therefore remain independent enforcement layers. Controllers and frontend modules consume the resolved context; they do not resolve or trust tenant identifiers themselves.

The application shell does not present organization context as a routine navigation control. Multi-organization entry selects a workspace before navigating to the same neutral application routes.

## Consequences

- Same-origin React requests need no tenant-specific route segment or custom header.
- The protected cookie is not an authorization grant: membership and tenant status are revalidated on every scoped request.
- Deep links remain stable when a customer-facing workspace handle changes.
- Subdomain or custom-domain lookup can later become another request adapter without changing application use cases or RLS initialization.
