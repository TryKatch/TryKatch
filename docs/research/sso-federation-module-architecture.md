# Trykatch SSO and federation module architecture

Status: proposed implementation design
Date: 2026-09-08
Scope: clean-room, free/open-source, production-oriented SSO for the Trykatch template

## Decision

Build SSO as an optional full-stack `Trykatch.Modules.Federation` module whose protocol and administration implementation can be installed or omitted, while keeping the security-sensitive completion of an external sign-in inside the non-replaceable Trykatch identity kernel.

The first adapter is generic OpenID Connect (OIDC), exercised against a pinned Keycloak development container. An operator can create and test a trusted provider connection by entering an issuer, client identifier, client secret, and a small policy form; neither application code nor the React build changes when a new connection is added. SAML is a later, separately selectable adapter, not part of the first UAT.

This is a better fit than copying Coolify literally. Coolify exposes named provider cards under Settings > OAuth, but its current v4 SSO surface supports only Authentik, Clerk, and Zitadel and explicitly does not offer generic OIDC configuration. It also documents email-based account matching and no provider-group-to-role mapping. Trykatch should copy the approachable enable/test experience, not those limitations. [Coolify SSO overview](https://next.coolify.io/docs/core/security/authentication/sso/overview), [Coolify OAuth overview](https://next.coolify.io/docs/core/security/authentication/oauth/overview)

### What the Coolify reference actually does

The current Coolify administration flow is intentionally small: a root team owner/administrator opens **Settings → OAuth**, selects a named provider, enters a client ID and secret plus provider-specific base/callback information, saves, and then turns on **Enabled**. For Authentik, for example, Coolify asks for Client ID, Client Secret, Redirect URI and Base URL; its guide explicitly says to save before enabling. Its login page then shows the enabled provider. Coolify also recommends testing in a private window with a non-root account and retaining an administrator recovery login before relying on SSO. [Coolify Authentik setup](https://next.coolify.io/docs/core/security/authentication/sso/authentik), [Coolify SSO test guidance](https://next.coolify.io/docs/core/security/authentication/sso/overview)

That interaction model is appropriate for Trykatch: a compact provider list, one focused configuration form, copyable callback URL, visible test/health status, and a separate enable action. Trykatch should improve the implementation underneath with generic OIDC, explicit identity links, scope-aware policy, encrypted secret references, and test-before-enforce.

## Why this is a useful modularity UAT

Federation crosses almost every seam a serious Trykatch module must support:

- backend registration and protocol callbacks;
- identity persistence and migrations;
- platform and organization permissions;
- public login UI and authenticated administration UI;
- secret storage, rotation, health checks, audit events, and telemetry;
- outbox-driven session and policy invalidation;
- optional development infrastructure through Aspire;
- generated OpenAPI and TypeScript client contributions.

It therefore proves substantially more than a cosmetic feature module. At the same time, the security kernel must remain in control of the final local identity, session cookie, organization access, RLS context, and authorization decision. A plugin is not a security boundary.

## Existing Trykatch baseline

Trykatch already has the right foundation:

- `TrykatchApp.Identity/DependencyInjection.cs` configures ASP.NET Core Identity, secure `__Host-` cookies, confirmed email, lockout, security-stamp invalidation, PostgreSQL-backed Data Protection keys, and an OpenIddict server/validator.
- The first-party React application receives only the HttpOnly application cookie and uses antiforgery protection.
- `OrganizationScopeMiddleware` derives an authorized workspace context from the authenticated actor and server-protected workspace cookie before organization-scoped code runs.
- The platform permission catalog already contains `platform.authentication.read` and `platform.authentication.manage`.
- ADR 0011 makes organization resolution, RBAC, PostgreSQL RLS, auditing, and module validation a non-replaceable security kernel.
- `ITrykatchModule` and the explicit backend/web catalogs provide build-time activation without arbitrary assembly loading.

What is missing is the external OIDC **client** flow, provider configuration, explicit external-account links, federation policy, and SSO management UI. Trykatch currently uses OpenIddict as an authorization **server** for external clients; that is different from Trykatch acting as an OIDC relying party/client to an enterprise identity provider.

## Module placement and deep interfaces

### Security kernel responsibilities

The kernel owns behavior that no optional module may bypass:

1. Validate the external protocol result and bind it to the exact trusted connection and login transaction.
2. Resolve an external subject to one global `ApplicationUser` using `(connection_id, issuer, subject)`, never email alone.
3. Apply invitation/JIT/domain and organization-access policy.
4. Create or link the ASP.NET Identity external login and issue the existing Trykatch application cookie.
5. Run local suspension, security-stamp, platform-access, membership, and organization checks.
6. Clear or set workspace context only after membership authorization succeeds.
7. Audit the effective human actor, connection, organization, outcome, and reason without recording tokens or secrets.

OpenID Connect defines `iss` and `sub` as the stable issuer/subject identity, while email and preferred username are not guaranteed stable or unique. ASP.NET Core Identity already supports an explicit external `UserLoginInfo` link through `UserManager.AddLoginAsync` and sign-in via a registered external login. [OpenID Connect Core](https://openid.net/specs/openid-connect-core-1_0.html), [ASP.NET Core `AddLoginAsync`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.identity.usermanager-1.addloginasync?view=aspnetcore-10.0), [ASP.NET Core `SignInManager`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.identity.signinmanager-1?view=aspnetcore-10.0)

### Optional module responsibilities

`Trykatch.Modules.Federation` owns:

- provider connection CRUD, enable/disable and test-before-enable;
- generic OIDC adapter registration and discovery caching;
- platform and organization SSO policy administration;
- login-button/home-realm-discovery contributions;
- claim normalization into a small provider-neutral result;
- encrypted credential persistence through the kernel secret seam;
- provider health/readiness details;
- federation-specific audit definitions, outbox events, OpenAPI contracts, React routes, and tests.

The external seam should be small and deep:

```csharp
public interface IExternalIdentityBroker
{
    Task<ExternalChallenge> BeginAsync(
        ConnectionPublicId connection,
        LocalReturnPath returnTo,
        CancellationToken cancellationToken);

    Task<ExternalIdentityResult> CompleteAsync(
        ExternalCallback callback,
        CancellationToken cancellationToken);
}
```

Callers must not know OIDC endpoints, state/nonce/PKCE storage, claim mappings, metadata refresh, secret retrieval, or provider-specific quirks. The interface returns a normalized result containing connection identity, exact issuer, subject, verified-email state, display claims, authentication time and assurance metadata. A separate internal adapter interface exists only inside the federation implementation. This provides leverage and locality: protocol fixes apply to every configured provider and the kernel tests the same seam used by production.

Do not expose a general `RegisterAuthenticationScheme(string, object)` interface. It would be shallow, difficult to validate, and would allow modules to weaken authentication.

## Provider and policy model

### Federation connection

Store public configuration and lifecycle metadata in the `identity` schema:

- immutable `Id`, opaque `PublicId`, `Scope` (`Platform` or `Organization`), optional `OrganizationId`;
- `Protocol` (`oidc` initially), display name, normalized exact issuer, client identifier;
- secret reference and secret version, never the secret value;
- requested scopes from an allowlist (`openid profile email` by default);
- claim mapping version, enabled state, tested state, configuration version;
- created/updated/enabled/tested actor and timestamps;
- last successful metadata refresh, last failure class, next retry time;
- concurrency token.

Every table holding organization-owned federation configuration includes `OrganizationId` and is protected by the same forced RLS and runtime-role rules as other organization data. Public login discovery must use a narrowly scoped, security-definer-free lookup that returns only display-safe enabled-provider metadata; it must never expose client IDs, issuers for hidden connections, domains, or secret references indiscriminately.

### Two distinct scopes

**Platform SSO** authenticates administrators of `/dashboard`. Only a role holding `platform.authentication.manage` may configure it. Organization administrators cannot affect it.

**Organization SSO** authenticates members into one workspace. It is owned by that organization and managed with new code-defined permissions such as `identity.federation.read`, `identity.federation.manage`, and `identity.federation.test`. Platform administrators can support it through an explicit audited support capability, not an implicit bypass.

A platform connection must never automatically grant platform access. An organization connection must never create platform access. External identity proves who the person is; Trykatch RBAC still decides what that person can do.

### Federation policy

Policy is explicit and versioned per scope:

- `SignInMode`: `Allowed`, `Preferred`, or `Required`;
- `ProvisioningMode`: `ExistingLinksOnly`, `InvitationOnly` (default), or `VerifiedDomainJit`;
- allowed connection IDs;
- allowed verified domains and the low-privilege default role for JIT;
- whether provider MFA/assurance claims are required for defined high-risk operations;
- local-login exception identities for recovery;
- session action when a provider/policy is disabled (`Keep`, `Revalidate`, `Revoke`).

Do not synchronize arbitrary IdP group names directly to powerful roles in v1. A later claim-mapping adapter may map allowlisted, immutable group/object IDs to Trykatch role IDs with preview, conflict detection, least-privilege defaults, and an auditable reconciliation job.

## Configuration without source-code changes

There are two different kinds of plugability and Trykatch should make the distinction visible:

1. **Install-time module selection:** the federation package contributes backend, migrations, permissions, UI, docs and tests through the existing explicit catalogs. This may require a build/deployment and is deliberate.
2. **Runtime provider connection:** once the OIDC adapter is installed, administrators add Authentik, Keycloak, Zitadel, Microsoft Entra ID, Okta, or another standards-compliant issuer by configuration only. No source edit or new React component is needed.

The UI belongs under **Authentication → Single sign-on**, with a simple list of connections and a focused create/edit page:

1. Name and scope.
2. Issuer/discovery URL.
3. Client ID and one-way-write client secret.
4. Generated exact callback URI with copy button.
5. `Test configuration`.
6. Provisioning policy.
7. `Enable` only after a successful current-version test.

Provider secrets are never sent back to React. The edit screen displays `Configured`, its version/last-rotated timestamp, and Replace/Rotate controls.

OpenIddict's web-provider client supports more than 100 named providers and multiple instances, and recommends a distinct redirect URI per instance to reduce mix-up risk. It also uses server metadata for protocol interoperability. [OpenIddict web providers](https://documentation.openiddict.com/integrations/web-providers)

However, arbitrary dynamic client registrations are not yet a first-class supported OpenIddict feature. Trykatch currently pins OpenIddict 7.7.0 in its generated lockfiles, so this limitation must be treated as an implementation constraint rather than hidden behind the UI. The maintainer's preferred interim approach is to populate `OpenIddictClientOptions` with `IConfigureOptions` and reload through `IOptionsChangeTokenSource`; deriving `OpenIddictClientService` is another possible but more invasive approach. The same discussion warns that accepting an arbitrary user-supplied issuer means trusting that server. A separate OpenIddict issue tracks first-class dynamic client registration for a future 8.0 preview, so Trykatch must not build its v1 contract on that unreleased behavior. [OpenIddict dynamic-registration discussion](https://github.com/openiddict/openiddict-core/issues/2192), [OpenIddict dynamic-registration tracking issue](https://github.com/openiddict/openiddict-core/issues/2404)

Therefore the implementation should:

- use OpenIddict Client behind `IExternalIdentityBroker`;
- load only enabled, administrator-approved database connections into immutable configuration snapshots;
- publish a new snapshot atomically after a tested configuration change;
- retain the previous connection version until all outstanding login transactions expire, so an in-flight callback cannot be reinterpreted under new settings;
- use a distinct callback route per opaque connection and configuration version;
- permit HTTPS issuers only outside a clearly marked local-development mode;
- reject private, loopback, link-local and cloud-metadata network destinations in production unless an operator-level outbound-network policy explicitly allows them, preventing issuer-discovery SSRF;
- place strict timeouts, response-size limits, redirect limits and circuit breaking around metadata and token endpoints.

If runtime reload proves unreliable under concurrency, the supported operational fallback is a coordinated application restart after saving configuration. That still meets “no source-code changes” and is safer than silently using stale or mixed configuration. The UAT must prove whichever activation mode is shipped.

## Protocol rules

Use Authorization Code flow with PKCE S256 even for the confidential web client. Require state, nonce, exact callback matching, issuer binding, audience validation and one-time authorization-code use. Do not enable implicit or password grants. RFC 9700 recommends PKCE for confidential clients, requires CSRF and mix-up defenses, prohibits open redirectors, and describes distinct redirect URIs as one mix-up defense. [OAuth 2.0 Security BCP, RFC 9700](https://datatracker.ietf.org/doc/rfc9700/)

OIDC discovery must retrieve metadata over TLS, require that the configured issuer exactly matches both the discovery document's `issuer` and the ID token `iss`, and abort on any validation failure. [OpenID Connect Discovery](https://openid.net/specs/openid-connect-discovery-1_0.html)

React never receives IdP access tokens, refresh tokens, client secrets, state, nonce, or PKCE verifier. The backend completes the exchange, discards provider tokens unless a separately approved downstream-integration use case requires them, links the local account, and emits the existing Trykatch application cookie. SSO must not turn the SPA into a token client.

## Account linking and provisioning

### Existing linked identity

The normal sign-in key is `(connection_id, issuer, subject)`. If its linked local account is active and still eligible for the requested scope, sign in and issue the local cookie.

### Existing local account without a link

Never silently link solely because the provider returned the same email address. Use one of these controlled paths:

- the already signed-in user opens Profile → Connected accounts, passes recent-authentication/step-up, and explicitly links the provider; or
- an invitation created for the same normalized email contains a one-time link transaction and the provider asserts `email_verified=true`; or
- an administrator creates a pending link that the user must confirm from both the local session and provider session.

Coolify's current documentation still describes email matching, but a recent Coolify source change moved toward an explicit `(provider, provider_user_id)` link and verified-email checks. Trykatch should start with the stronger model. [Coolify OAuth overview](https://next.coolify.io/docs/core/security/authentication/oauth/overview), [Coolify explicit-provider-link change](https://github.com/coollabsio/coolify/commit/607cb1003e22bd484f6949cb0cf405b7fceea6f2)

### Invitation-only provisioning (default)

An organization invitation is accepted only when:

- invitation token is valid, unexpired, unconsumed and bound to the target organization;
- external issuer/subject result came from one of that invitation's allowed connections;
- provider asserts a verified email and it equals the invitation email after canonical normalization;
- no conflicting external link exists;
- the invited roles are still valid and grantable by the inviter.

The global user is created or linked, membership and roles are created atomically, the invitation is consumed once, and audit/outbox records are committed in the same transaction.

### Verified-domain JIT (optional, off by default)

JIT requires ownership verification of the domain, an IdP-specific stable identity, a verified email, a least-privilege default role, and a maximum-seat/rate policy. A matching email suffix alone is not proof that an organization controls the IdP or domain. Domain verification and JIT should not block the first UAT.

## Secret storage and rotation

Define a narrow kernel seam such as `IFederationCredentialVault`; module tables store only an opaque reference and version.

Recommended adapters:

- **Development/UAT:** an ASP.NET Core Data Protection adapter with a unique, versioned purpose chain and the existing PostgreSQL key ring. The key ring must be backed up, shared by all API instances, and encrypted at rest before production. Microsoft documents automatic key management/rotation and purpose isolation, but also notes that Data Protection is not primarily designed for indefinite confidential storage. [Data Protection overview](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/introduction?view=aspnetcore-10.0), [purpose strings](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/consumer-apis/purpose-strings?view=aspnetcore-10.0), [key storage guidance](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/implementation/key-storage-providers?view=aspnetcore-10.0)
- **Portable production default:** envelope-encrypted database blobs, with the key-encryption key supplied as a mounted secret and never stored in the database/image/source. Rotation writes a new credential version, validates it, switches current/next atomically, and retires the previous version after a bounded overlap.
- **Optional external adapter:** OpenBao, an open-source MPL-2.0 secret manager supporting secure secret storage, rotation-oriented leases, and auditability. [OpenBao](https://github.com/openbao/openbao), [OpenBao lease model](https://github.com/openbao/openbao/blob/main/website/content/docs/concepts/lease.mdx)

Microsoft configuration guidance says never place passwords or sensitive values in source or plain-text configuration files; User Secrets is for development, while production secrets should come from an external provider. [ASP.NET Core configuration](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/?view=aspnetcore-10.0), [development secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets?view=aspnetcore-10.0)

Operational requirements:

- one-way-write secret UI and automatic redaction in logs, traces, Problem Details and audit metadata;
- current and next versions for zero-downtime rotation;
- no long-lived decrypted cache; keep decrypted values in memory only for the exchange and clear references promptly;
- optimistic concurrency and a recent-authentication requirement for secret replacement;
- an audit event for create, test, enable, disable, rotate, rollback and delete-reference actions;
- never include client secrets or provider tokens in the transactional outbox.

## Lockout, recovery and offboarding

SSO may be `Required` only after a successful private-window test by a non-break-glass account. Trykatch must preserve at least one tested local platform recovery administrator whose credentials and MFA recovery material are stored operationally outside the application. The product must refuse to remove the final recovery path.

Provider MFA is not automatically equivalent to Trykatch's local MFA policy. Record and interpret `amr`, `acr` and authentication time only through an allowlisted per-provider assurance mapping. High-risk Trykatch operations may still demand a fresh provider reauthentication or local step-up.

Disabling a provider stops new challenges immediately and invalidates outstanding login transactions. It does not silently delete local users or memberships. The administrator chooses whether existing sessions are retained, marked for revalidation, or revoked by rotating security stamps. Coolify likewise warns that removing IdP access alone does not remove active sessions, memberships, passwords or tokens, so offboarding must be completed locally. [Coolify SSO overview](https://next.coolify.io/docs/core/security/authentication/sso/overview)

## Audit, observability and health

Emit structured, stable event codes:

- `federation.connection.created|tested|enabled|disabled|rotated|deleted`;
- `federation.login.started|succeeded|failed` with coarse reason categories;
- `federation.account.linked|unlinked`;
- `federation.provisioning.invited|jit_created|rejected`;
- `federation.policy.changed` and `federation.sessions.revoked`.

Record connection ID, configuration version, scope, organization ID when applicable, local actor ID when known, correlation/trace ID, result category, and safe IdP error class. Never record authorization codes, tokens, state, nonce, PKCE verifier, secret values, full claims payloads, or raw provider error bodies.

OpenTelemetry spans should cover discovery, challenge creation, callback validation, token exchange, link resolution, policy evaluation and cookie issuance. Metrics include attempts/outcomes by provider and scope, latency, discovery age, metadata-refresh failures, invalid-state/nonce/issuer counts, and provisioning outcomes. Provider names must be bounded labels; email, subject and organization IDs must not be metric labels.

`/health/live` does not depend on an IdP. Readiness verifies that the module can read its own configuration and credential-vault metadata, but an external provider outage should normally produce a degraded provider status rather than taking the whole API out of rotation. The Authentication page shows per-connection health and the last successful test.

## First UAT acceptance test

Use a pinned Apache-2.0 Keycloak container as an Aspire development resource. Keycloak publishes standard OIDC discovery endpoints and is Apache-2.0 licensed. [Keycloak OIDC endpoints](https://github.com/keycloak/keycloak/blob/main/docs/guides/securing-apps/partials/oidc/available-endpoints.adoc), [Keycloak repository/license](https://github.com/keycloak/keycloak)

The first UAT is complete only when all of the following pass automatically and can be repeated manually:

1. Generate and build a default Trykatch app with federation disabled. Its SSO backend controllers, EF model, OpenAPI operations, React route and login option are absent.
2. Generate with `--sso oidc` (or install the module through the future module CLI). The package contributes backend registration, migration, permissions, OpenAPI, React route and Aspire Keycloak resource without editing feature files manually.
3. Start PostgreSQL, API, web and Keycloak. Seed one realm/client, one verified invited user, one verified but uninvited user, one unverified user, and two Trykatch organizations.
4. As a platform authentication administrator, create a disabled connection from the UI using only display name, issuer, client ID and client secret. The UI shows the exact callback URL and never reads the secret back.
5. `Test configuration` validates TLS/development exception, discovery, exact issuer, supported code flow and PKCE, JWKS retrieval and a real non-root private-browser round trip. Only then can the connection be enabled.
6. The invited user follows the organization invitation, authenticates in Keycloak, becomes linked by `(connection, issuer, subject)`, receives the existing secure Trykatch cookie, lands in the inferred organization workspace, and has exactly the invited organization role. No bearer/refresh token appears in React storage, JavaScript or browser-readable cookies.
7. The same user signs out and signs back in using the new SSO button; the existing global user and membership are reused. A duplicate account is not created.
8. The uninvited verified user, unverified user, wrong-audience token, wrong-issuer metadata, replayed callback, altered state, altered nonce, expired code and mismatched invitation email are rejected with non-enumerating UI errors.
9. An Organization A connection cannot authenticate or read configuration for Organization B. PostgreSQL integration tests prove RLS blocks cross-organization access even when application filters are missing.
10. Rotate to a second client-secret version, prove new logins work on two API replicas, and prove an in-flight callback created under the previous tested version completes only within its bounded overlap. After retirement, the old secret cannot be used.
11. Disable the provider and prove no new challenge starts, outstanding transactions fail closed, configured session policy executes, and the tested local recovery administrator still signs in.
12. Logs, traces, audit events, outbox records, generated OpenAPI and browser storage are scanned to prove no secret, authorization code, token, state, nonce, PKCE verifier or raw claims payload escaped.
13. Package/template tests cover dotted and hyphenated application names and both UI/default and `--ui none` output.

## Adopt now, optional later, avoid

### Adopt now

- Generic OIDC provider adapter using Authorization Code + PKCE.
- Platform and organization connections with explicit separation.
- Invitation-only provisioning, explicit `(issuer, subject)` account links and verified-email checks.
- Connection-versioned immutable runtime snapshots and distinct opaque callbacks.
- Test-before-enable, private-window UAT and a protected local recovery path.
- One-way-write encrypted credentials, versioned rotation, audit and health status.
- SSO UI under Authentication, with one compact list and focused configuration flow.
- Keycloak Aspire resource for repeatable free local UAT.

### Optional later

- SAML 2.0 adapter after OIDC is stable. `ITfoxtec.Identity.Saml2` supports .NET 10 and is BSD-3-Clause; `Sustainsys.Saml2` is MIT, but its v3 architecture is still under active development. Select and pin one only after a threat review and interoperability suite. [ITfoxtec.Identity.Saml2](https://github.com/ITfoxtec/ITfoxtec.Identity.Saml2), [Sustainsys.Saml2](https://github.com/Sustainsys/Saml2)
- Verified-domain JIT provisioning.
- SCIM provisioning/deprovisioning with its own service credentials and reconciliation ledger.
- Allowlisted IdP group/object-ID mappings with preview and explicit deny rules.
- Passkeys/WebAuthn and provider-assurance-based step-up.
- OIDC front-channel/back-channel logout only after local session semantics are reliable.
- OpenBao credential-vault adapter.

### Avoid

- Email-only implicit account matching.
- Auto-granting platform or owner roles from external claims.
- Accepting arbitrary issuer URLs from unauthenticated users.
- Browser-held IdP tokens or secrets.
- Runtime loading of arbitrary authentication assemblies.
- Letting a module replace cookie issuance, organization resolution, RLS or authorization.
- Enabling a provider before an independent recovery login and negative-path tests pass.
- Persisting full claims or tokens “just in case.”
- Implementing both OIDC and SAML in the first iteration.

## Free/open-source dependency position

| Capability | Recommended choice | License/status |
|---|---|---|
| OIDC protocol client | Existing OpenIddict Client packages | Apache-2.0; already present transitively in Trykatch lockfiles |
| ASP.NET Identity/cookies/Data Protection | ASP.NET Core shared framework | MIT |
| Local enterprise IdP for UAT | Keycloak pinned container | Apache-2.0 |
| Optional external secret manager | OpenBao | MPL-2.0, open source |
| Later SAML adapter | ITfoxtec.Identity.Saml2, subject to security review | BSD-3-Clause, .NET 10 support |

This design introduces no mandatory paid package or service. Commercial IdPs remain compatible through standard OIDC, but Trykatch's UAT and default development stack use only free/open-source software.

## Conclusion

Yes, Trykatch can offer the “enter client ID and secret, test, enable, and it works” experience. The maintainable design is not a universal secret-driven plugin that executes arbitrary code. It is a small, trusted set of compiled protocol adapters behind a deep identity-broker interface, combined with runtime provider connections stored as validated, versioned configuration.

That separation gives operators configuration freedom without allowing modules to weaken the security kernel, and it makes federation an excellent first UAT of Trykatch's full-stack modular architecture.

The same architecture generalizes to other third-party integrations: install a reviewed adapter package for a protocol or vendor family, then create any number of runtime connections containing validated public settings and opaque versioned secret references. API keys are configuration for an installed adapter, not executable plugins. This lets future SMTP, storage, payment, webhook, search, and AI-provider connections be added without feature-code edits while preserving allowlists, authorization, rotation, health and audit controls in one deep connection-management module.
