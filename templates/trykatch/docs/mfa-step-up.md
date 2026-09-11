# MFA recent-assurance and enrollment

Pending MFA secrets use the same durable Data Protection ring as cookies and antiforgery. [Production identity maintenance](production-identity.md) preserves that ciphertext across restart and certificate rotation; retain required private certificates and do not reset the ring. Existing enrollment expiry still applies.

Account security is owned by the `IAccountSecurity` application boundary and its Identity implementation. Controllers supply the authenticated user, the protected cookie ticket's session identifier, and the cookie security stamp; request JSON cannot supply that authority. Browser requests require the application cookie and antiforgery token. Bearer tokens are not accepted for these flows.

## Verification and lifecycle

1. `POST /api/v1/account/security/reauthenticate` accepts `purpose`, `password`, optional `code`, and `isRecoveryCode`. A local account must prove its password, and an enrolled account must additionally prove its current authenticator or an unused recovery code. Wrong proofs use the account's existing five-attempt/15-minute lockout, with a generic failure. An account-scoped, per-process limiter allows 30 security requests per five minutes; the persisted lockout remains shared across replicas.
2. The response contains a cryptographically random, opaque `grant` and `expiresAt`. Only its SHA-256 hash is stored. A grant expires after five minutes, is single-use, and is bound to user, cookie session, security stamp, and one allowlisted purpose. Never put it in a URL, log, analytics event, persisted cache, or browser storage.
3. `mfa.enroll` or `mfa.replace` authorizes `POST /mfa/setup` with `{ "grant": "…" }`. Setup returns only a **new pending** `enrollmentId`, `sharedKey`, `authenticatorUri`, and `expiresAt`. It never returns the active authenticator key. The pending key is protected with a dedicated ASP.NET Core Data Protection purpose and stored separately for at most ten minutes of authorization validity. There is one pending enrollment per user.
4. `POST /mfa/enable` accepts `enrollmentId` and the new authenticator `code`. Confirmation must use the originating cookie session and unchanged security stamp. The old authenticator and recovery codes remain valid until confirmation succeeds. Five invalid confirmation attempts invalidate the pending enrollment; expiry, cancellation, repeated confirmation, and another session cannot activate it. `POST /mfa/cancel` accepts `enrollmentId` and discards only that session's pending setup.
5. `mfa.recovery-codes` authorizes `POST /mfa/recovery-codes`; `mfa.disable` authorizes `POST /mfa/disable`. Both require `{ "grant": "…" }`. A grant for one action cannot authorize another.

All security mutations acquire the same user-keyed PostgreSQL transaction advisory lock and re-read the user after acquiring it. Grant consumption, pending enrollment, Identity updates, and successful audit outcomes share that transaction. Failed Identity/storage updates roll back the complete mutation; invalid proofs commit their failure counters and safe outcome. Recovery-factor persistence failures are not treated as ordinary wrong factors.

Confirmation and recovery regeneration return ten recovery codes once. Replacement invalidates old codes. Disabling clears recovery codes and resets the authenticator key. All three completion paths rotate the security stamp, remove outstanding grants and pending enrollment, and deliberately sign out the current session. The UI hands returned codes to a dedicated root-level completion state outside both authenticated shells. It unmounts the shells so window refocus/session expiry cannot redirect away and erase the only copy. Codes remain only in memory until explicit acknowledgement; they never enter URLs, browser storage, history state, or query caches. Leaving/reloading requests a browser warning while codes are unacknowledged; a full reload still loses them by design. Other cookies are rejected immediately at this account-security boundary; other application endpoints retain the configured five-minute security-stamp validation interval.

The pending verifier follows RFC 6238's six-digit SHA-1 format and the same two-step clock tolerance as [ASP.NET Core Identity's authenticator provider](https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Identity/Extensions.Core/src/AuthenticatorTokenProvider.cs). Successful enrollment is also tested through the unmodified Identity MFA sign-in endpoint.

## Stable failures

| Problem code | HTTP | Meaning |
| --- | --- | --- |
| `reauthentication_required` | 403 | Missing, used, wrong-purpose, wrong-user/session/stamp grant, or stale session. Sign in again if needed, then obtain new proof. |
| `reauthentication_failed` | 403 | Wrong password/factor, or locked account. No factor-specific detail is disclosed. |
| `grant_expired` | 403 | Grant or pending enrollment expired. Start again. |
| `enrollment_conflict` | 409 | A setup is already pending or cannot be confirmed/cancelled in this session. Wait ten minutes if the original session was lost. |
| `reauthentication_unsupported` | 422 | No supported local password credential. |
| `identity_validation` | 400 | A required Identity update failed; no partial security change committed. |

Security responses are `Cache-Control: no-store`. The React panel translates stable outcomes into English/French copy and does not render raw server error details. Grants and passwords are not placed in React Query state or browser persistent storage.

## Deployment and recovery

- Apply `AddAccountSecurityStepUp` with the existing privileged migrator before deploying the new API. The migration adds `identity.recent_assurance_grants`, `identity.pending_mfa_enrollments`, and `identity.account_security_events`; it does not modify existing authenticator keys, MFA flags, passwords, recovery codes, or cookies. Run the usual runtime-grant phase so the Identity runtime role can access the new tables. Existing enrolled users can sign in with their current authenticator/recovery codes after migration.
- Deploy API and regenerated clients together. Drain old API instances: an old instance would still expose the previous, unprotected setup behavior. Existing cookies without a session identifier may continue ordinary application use, but must sign out/in before recent-assurance operations. New sign-ins receive a new protected session identifier; sliding renewal preserves it.
- Keep Data Protection keys available across replicas and restarts. Pending keys are ciphertext, but this PR does not encrypt the Data Protection key ring itself; certificate-protected key-ring provisioning and migration remain PR05. Loss of a pending key's decrypting key requires restarting enrollment after expiry; the active authenticator is unaffected.
- If a user loses an authenticator but has a recovery code, sign in and use password plus an unused recovery code to replace it. If both factors and recovery codes are lost, there is deliberately no cookie-only reset. Follow an organization's separately approved, identity-verified recovery process. Never clear MFA based solely on possession of an existing cookie.
- Accounts without a local password return `reauthentication_unsupported`; no equivalent external identity-provider reauthentication is currently implemented. The administrator must use the provider's verified recovery path. This feature does not silently accept an external cookie as recent assurance.
- Back up account-security audit events before any rollback. The down migration drops the three new tables, including their audit records; rolling back to old security endpoints reintroduces the original weakness. Prefer forward repair.

Account-security audit records contain only user ID, server-defined action/outcome, and time—never passwords, codes, secrets, tokens, session identifiers, or raw exceptions. They are global Identity events, not organization-scoped business audit entries. Expose/export them only through an independently authorized operator workflow; a new audit viewer and retention scheduler are outside this change. Expired grants are pruned on subsequent reauthentication and expired pending state is replaced on subsequent setup. Operators may purge expired transient records under their retention policy without deleting audit history.
