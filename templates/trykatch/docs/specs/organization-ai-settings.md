# Organization AI configuration

## Outcome and scope

Organization managers activate or disable read-only AI Help from Administration → Settings → AI Configuration. The page uses the application's existing tab navigation and branding. Each organization decides independently; new and existing organizations default to disabled.

The user additionally requires provider/subscription configuration from the same UI. This iteration includes organization-owned provider, exact model ID, operator-approved endpoint, timeout, write-only key replacement/removal and an explicit connection test. [ADR 0014](../adr/0014-organization-ai-provider-settings.md) extends ADR 0013 with encrypted organization-bound storage and destination enrollment. Anonymous/public AI, daily billing quotas and arbitrary environment-variable editing remain excluded. No pasted credentials are used.

## Ownership and rules

The host's organization control plane owns one activation record per organization. Organization and actor identities come from authenticated context, not the body or URL. A missing record is disabled with an empty version. Settings changes require the current version, revalidate management authority under the organization management lock, and write an audit intent in the same transaction.

Effective activation requires organization opt-in and either valid saved organization provider settings or the explicitly enabled platform provider when no tenant override exists. Invalid tenant overrides never fall back. `Assistant:AllowTenantConfiguration` permits tenant setup by default; operators can disable it. The backend checks opt-in before every assistant turn. Provider/activation changes invalidate old continuation fingerprints. Disabling does not erase records or send a provider request. Connection tests are explicit, billable, management-only and send no business data.

## Permissions

View configuration: `organizations.read` or `organizations.manage`. Change activation: `organizations.manage`, revalidated in the use case. No ordinary-member grant is added. Current seeded Admin has read access; Owner can enable or delegate management through existing role administration.

## Implementation

New activation entity/store and forward platform migration with forced organization RLS. Existing assistant runtime consumes a narrow activation contract; provider protocols and read-tool security stay unchanged. New Settings route and navigation use real shared components, translated labels, isolated Storybook fixtures and generated API clients.

## Acceptance cases

- Missing record or unresolved context never enables a provider call (runtime/application tests).
- Organization A changing activation does not enable B; foreign/missing database scope rejects access and writes (PostgreSQL tests).
- Unauthorized or revoked manager cannot change activation; stale version returns conflict without audit/write (application/HTTP tests).
- Cookie mutation without antiforgery is rejected; unknown request fields cannot select another organization (HTTP tests).
- Approved provider settings are used for this organization's inference; another organization's client/options/key remain unchanged. Disabled/invalid configurations never fall back (runtime/HTTP tests).
- Keys are ciphertext-only in storage and never returned; keep/replace/remove semantics, changed-destination replacement and purpose-bound decryption reject foreign keys (unit/PostgreSQL/HTTP tests).
- Unapproved endpoints and malformed/control-bearing keys fail before encryption or network calls; connection tests use a saved version with no business data (HTTP/protocol tests).
- UI distinguishes loading, read-only, server-unavailable, saving, error and conflict; failed saves preserve the chosen value (unit/Storybook/browser tests).
- Responses and logs contain no provider credentials (contract tests/review).

## Verification record

All 12 host architecture checks and the English/French documentation-site build pass. The template-owned specification and verification skills guided the authorization, RLS and catalogue evidence requirements.

Provider configuration verification: all 441 backend unit tests and 12 architecture checks pass. The expanded real PostgreSQL/production-HTTP scenario passes without skips: organization-bound encrypted key storage, foreign-purpose decryption rejection, approved-provider inference using the tenant model/key, retained/replaced/removed keys, changed-provider replacement enforcement, invalid destinations rejected without mutation/network calls, stale versions, unauthorized tests, eight-token saved-configuration probes without business data/tools, no global credential fallback, disabled second-organization inference and suspended membership selection rejection. It uses fake provider responses and fake keys, not a paid service.

All 91 host UI tests and 138 source Chromium Storybook interaction/accessibility checks pass, including 11 settings UI regressions, 14 settings stories and six shared floating-dropdown stories. Workspace typecheck, generated client regeneration, production build (with Storybook assets excluded), Storybook build and English/French documentation build pass. The compact branded provider dropdown supports keyboard selection and Escape/focus recovery. A host regression protects 38 px controls, transparent floating labels and absence of extra focus rings. Repository guidance requires reuse of shared floating controls; generator regressions protect text-like field rendering. Browser checks cover light/English and dark/French at 320/768/1440 px with the menu inside the viewport and no horizontal overflow; these are mocked catalogue checks, not real application/provider UAT. Uploading stories wait until their fixture receives the request before ending, preventing delayed requests from escaping into another story.

Audit follow-up: two real PostgreSQL regressions pass without skips. Settings reject a previously resolved manager after another request revokes the persisted permission; a database-trigger failure at the actual audit insert boundary rolls back the settings version and activation, leaving only the prior committed audit intent. The invitation interaction additionally asserts that its selected role ID is posted and shown in the receipt. The settings dropdown passed all twelve light/dark × English/French × 320/768/1440 px combinations, with 38 px controls, no horizontal overflow and a viewport-bounded menu. All 138 source Storybook checks and the English/French documentation build pass again.

Fresh local package qualification passed: a private-hive React application generated a shipment-reception workflow and invoicing CRUD module with `--with-web`. Both completed backend/frontend builds, module unit/architecture tests, generated API clients and doctor validation. The generated application's real PostgreSQL workflow/default-deny isolation acceptance passed two tests without skips. All 138 generated-project Storybook interaction/accessibility checks passed without a backend. Browser checks passed the twelve settings catalogue combinations and twelve combinations each for the generated production sign-in, CRUD editor and workflow editor. Form checks exercise focus, fill, blur and clearing, transparent labels, compact controls, inline required markers and viewport-bounded layouts. Production module visual checks use explicit fake session/access/list responses; they do not prove authenticated end-to-end workflows or paid-provider UAT. Production output excludes Storybook-only assets. All 441 backend unit tests pass again after the preview.31 metadata bump.

Two broader local integration runs each had one initial PostgreSQL transport failure: 252 passed plus one EOF, then 254 passed plus one SSL negotiation response error, both with zero skips. All six rows of the first affected method and the second affected case passed on isolated reruns. These are not clean full-suite passes; Docker Desktop port readiness is suspected, not conclusively diagnosed. Successful clean CI and sealed release qualification remain required. Concurrent lock-order revocation races, production-provider UAT and the full application-wide visual matrix remain unverified. No publication or enterprise-certification claim.
