# 0003: Keep Identity as an in-process module

- Status: Accepted
- Date: 2026-09-07

## Decision

Place ASP.NET Core Identity and OpenIddict in a dedicated project composed into the API host. Use secure cookies for the first-party web application and standards-based OIDC for external clients.

## Consequences

There is no network hop for first-party authentication. The module can be extracted later if an independently deployable identity provider becomes necessary.

MFA lifecycle changes cross the `IAccountSecurity` application boundary. They require session-bound recent assurance and use separately encrypted pending enrollment, with serialized, transactional Identity updates. The active authenticator is never redisplayed by setup. See [MFA verification and deployment](../mfa-step-up.md) for the request contracts, session behavior, migration, and unsupported-provider recovery policy.
