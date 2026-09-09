# Threat model

Status: pre-release baseline  
Reviewed: 2026-09-08

## Scope and assets

This model covers the generated React web application, same-origin reverse proxy, ASP.NET Core API, Identity/OpenIddict module, PostgreSQL schemas and roles, migrator, outbox processor, and OpenTelemetry pipeline. Protected assets are user credentials and MFA material, signing/encryption keys, session and workspace-context cookies, organization data, platform administration rights, audit evidence, database backups, and telemetry that can contain operational metadata.

## Trust boundaries

1. An untrusted browser reaches only the web reverse proxy.
2. The proxy forwards /api and /connect to the private API network.
3. The API authenticates the actor, resolves server-protected workspace context, authorizes permissions, and starts database transactions.
4. The runtime database role crosses into PostgreSQL but cannot own objects or bypass RLS.
5. The one-shot migrator uses a separately controlled owner credential.
6. The API sends OTLP to the collector on the private single-host network; authenticated TLS is mandatory outside that trust domain. Grafana backends are operational infrastructure, not authorization systems.
7. External OIDC clients cross a protocol boundary and receive only the grants registered for that client.

## Required controls

| Threat | Control and verification |
|---|---|
| Cross-organization disclosure or write | Tenant-neutral routes; protected workspace cookie or trusted organization claim; membership revalidation; application predicates; transaction-local actor/organization values; forced PostgreSQL RLS; Testcontainers cross-organization tests. |
| Platform privilege escalation | Platform roles and permissions are separate from organization RBAC; a code-defined catalog rejects unknown grants; built-in roles are immutable; changing access updates the security stamp; the final active administrator is protected. |
| Session theft or fixation | Host-only Secure HttpOnly SameSite cookies; sign-in rotates the authentication session; workspace context is cleared at login/logout; short security-stamp validation; TLS is mandatory at the ingress. |
| Cross-site request forgery | All browser mutations require the antiforgery cookie/header pair; missing and stale-token behavior is integration-tested. |
| Credential attacks | Invitation-only accounts, confirmed email requirement, 12+ character policy, fixed-window request limiting, five-attempt lockout, TOTP MFA, one-time recovery codes, and generic invalid-credential responses. |
| OAuth/OIDC downgrade or token leakage | Authorization Code requires PKCE; client credentials are confidential; implicit and password grants are disabled; first-party React receives no bearer or refresh token; production keys come from mounted PKCS#12 files. |
| Database-owner compromise through the API | The API receives only the runtime role; bootstrap, migrator, and runtime secrets are distinct; the runtime role is NOINHERIT NOBYPASSRLS and owns no relations; CI verifies these invariants. |
| Destructive or unaccountable data changes | Archive and pending deletion are recoverable states; delete requires a reason; permanent disposal is outside normal CRUD; security-relevant actions produce immutable audit snapshots. |
| Duplicate integration effects | Transactional outbox delivery is at least once; every envelope carries a stable message ID; transport adapters and consumers must deduplicate with that ID; retry identity is unit-tested. |
| Secret disclosure in source, images, logs, or telemetry | ServiceDefaults projects console and OTLP logs onto a bounded approved schema; outbox errors export type, not message; Collector resource/attribute allowlists and body/status sanitization fail closed before persistent queues; secret files are mounted read-only; CI checks planted inputs and configuration. Custom fields require privacy review. |
| Supply-chain substitution | NuGet and pnpm lockfiles, central versions, vulnerability audits, SBOM generation, secret scanning, and tag-plus-digest container references. |
| Telemetry outage affecting business traffic | OTLP export is asynchronous; API startup has no Collector dependency; Collector queues/retries are bounded and file-backed for logs/traces; application readiness depends on PostgreSQL, not Grafana backends. Queue overflow, retry expiry, disk/host loss, and missed Prometheus scrapes remain explicit loss windows. |
| Public operational access | Collector, Loki, Tempo, and Prometheus have no host bindings. Grafana defaults to loopback with a non-default password and requires authenticated HTTPS ingress. Backend-only API ingress must deny `/health/*`; probes themselves remain unauthenticated on the private container network. |
| Forged forwarding headers | The React profile binds to loopback; its host ingress must remove client-supplied forwarding headers and derive the scheme from the authenticated connection. The API accepts one symmetric forwarding hop only from the exact configured web-container IP. Direct API publishing is a diagnostic profile and requires its own trusted-proxy configuration. |

## Residual risks and release blockers

- A production ingress configuration and certificate-rotation drill are deployment-specific and must be verified in the target environment.
- Backup/PITR and restore evidence cannot be proven by the template repository; the deploying team owns the recovery drill.
- Rate limits are single-process in version one. Multi-replica deployments that require a global quota must add a distributed limiter at the ingress or a shared adapter.
- The broker-free outbox adapter provides a safe local default, not external event delivery. A chosen broker adapter needs its own authentication, authorization, retry, retention, and consumer-idempotency review.
- Operational access to PostgreSQL, Grafana, Loki, Tempo, Prometheus, the container host, and the deployment secret store is outside application RBAC and must follow least privilege.
- Staging must prove planted-secret absence with positive telemetry controls, TLS/auth isolation, persistent-queue recovery/loss bounds, retention, cardinality, capacity, and alert delivery. Local validation is not production qualification.

Any unresolved high or critical finding blocks a release candidate. Revisit this model whenever a new module introduces personal data, file upload, outbound messaging, external identity, a public callback, or another persistent store.
