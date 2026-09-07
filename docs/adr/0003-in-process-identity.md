# 0003: Keep Identity as an in-process module

- Status: Accepted
- Date: 2026-09-07

## Decision

Place ASP.NET Core Identity and OpenIddict in a dedicated project composed into the API host. Use secure cookies for the first-party web application and standards-based OIDC for external clients.

## Consequences

There is no network hop for first-party authentication. The module can be extracted later if an independently deployable identity provider becomes necessary.

