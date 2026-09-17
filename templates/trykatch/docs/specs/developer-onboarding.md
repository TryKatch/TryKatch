# Developer onboarding delivered with the template

## Outcome and scope

Developers generating an application get a discoverable onboarding guide, suggested questions for their existing coding assistant, and a generation-time message. Covers architecture, installed module discovery, source module creation, backend/frontend composition, inter-module/external integration and appropriate verification. Full-stack and backend-only output both work. In-app AI Help remains unchanged.

## Evidence and decisions

The user chose repository-based developer onboarding rather than another developer chat/tab. Reuse existing AGENTS.md, five project-local skills, source-local CLI and version-matched documentation. No onboarding-specific AI provider, key, automatic assistant launch, setup or migrations. Guides are shipped in English and French; existing tools retain their normal configuration and data policies. A chat-only tool cannot inspect the checkout; explicitly provide reviewed excerpts instead.

## Ownership and rules

Owned by the template's development documentation and template-engine configuration, not a business module. No new API, permission, organization data, runtime configuration, database migration or generated contract. Guide content is scaffolded application-owned documentation; template updates do not retrofit old applications. Source project names follow the existing template replacement; skill IDs remain stable. Backend-only instructions explicitly skip frontend steps. No claim that a scaffold implements arbitrary integration/business requirements.

## Implementation

`docs/developer-onboarding.md` and its French counterpart link the installed documentation. README variants, AGENTS.md and the existing AI-development guide provide entrypoints. Two mutually exclusive UI-mode instruction post-actions print a short path/starter prompt using the official non-executing instruction action; the existing Git post-actions and CLI progress/failure handling remain intact. The CLI already displays successful engine output after stopping its spinner, so no duplicate presenter or command is needed. See [the .NET post-action registry](https://github.com/dotnet/sdk/blob/main/documentation/TemplateEngine/Post-Action-Registry.md).

## Acceptance cases

### First-run-to-first-feature milestone

The user approved the recommended next milestone: a concrete journey, reusing existing `doctor`, `setup`, `status`, Aspire startup and module creation rather than adding another overlapping CLI. Ship EN/FR `docs/first-feature*.md`, linked from onboarding. Equipment is explicitly a disposable educational CRUD catalog, not a rental workflow or a change to the running product. Explain generator output ownership, actual startup evidence, permissions/RLS, decimal transports, OpenAPI, languages and live validation checkpoints.

Qualify a fresh packed React application and a true `--ui none` application: run doctor/setup, assert unknown runtime without a URL, create Equipment with explicit fields and conditional web, retain generation diagnostics, run module doctor and a real PostgreSQL isolation test against the enabled catalog. Assert the actual test executed/passed, never a skipped or zero-test run. Web also checks assistant-contract freshness (`generate:check` does not check the TypeScript client). Add a CI qualification matrix and packaging classification for the guides. Business-field labels need translation review; EN/FR interface catalogs are not automatic domain translation. This harness does not start Aspire or certify live CRUD/browser behavior or a first-time developer's experience; those remain documented manual acceptance steps.

- Given either renamed packed template variant, when generated, both guides ship, internal links resolve, C# paths follow the chosen namespace and README/task router discover the guide. Node/file-system acceptance checks.
- Given React output, generation prints onboarding including its frontend distinction; backend-only output prints skip-frontend guidance. Real template-engine output checks.
- Given scripting is declined, the instruction-only onboarding message still prints; no assistant, setup or new executable action is activated. The existing Git script is refused, no Git directory is created, and the engine's expected post-action exit 105 is preserved rather than hidden. Real template-engine check.
- Given the source-local CLI `new` path, the same engine onboarding output is preserved after its existing progress completes. Real CLI generation check.
- Given an onboarding-only docs change, CI selects documentation and packed-template verification. Change-classification test.
- Guides cover source inspection, supported module/blueprint limits, generated ownership, stable contracts, server authorization/RLS, frontend/OpenAPI, events/outbox/external API policies and falsifiable verification. Content checks plus local review.

## Verification record

Verified locally on 2026-09-17:

- `node scripts/test-development-skills.mjs`: six source checks pass, covering discoverability, links, renamed source paths, guide workflow topics and instruction-only configuration. The new checks were first run failing before implementation.
- `TRYKATCH_KEEP_SKILL_WORKSPACE=true bash scripts/test-development-skills.sh`: package built and installed into an isolated template hive; renamed React and backend-only output each pass all six checks. Real generation logs contain the appropriate onboarding message. Backend-only generation uses the actual source CLI `new` path and preserves both completed progress and the engine's message.
- Declining scripts still prints onboarding and produces the existing expected engine exit 105 for refused Git initialization, with no `.git` directory. That generated output also passes all six checks. The harness asserts the refusal rather than converting arbitrary failure into success.
- `bash scripts/ci/test-detect-changes.sh` and shell syntax check pass. Onboarding-only changes select documentation and packaging gates.
- `corepack pnpm build` in the documentation site passes: zero errors/warnings/hints; English/French pages built.
- Source-local `module doctor` reports a healthy workspace; `git diff --check` passes. Local task review found no new runtime/API/schema/secret changes, duplicate CLI presenter or unsupported generator flag. Existing in-app assistant and unrelated work are preserved.

Acceptance artifacts are retained in the temporary `trykatch-skills.0NpnEy` workspace for inspection. No hosted-model calls, global template installation, product database changes, push, merge, release or deployment were performed. The feature is implemented locally, not published. A coding model's automatic discovery/compliance and actual business integrations are not certified by these file/CLI checks. Broad backend and browser suites were not rerun because this task changes packaged documentation and template instruction metadata, not application behavior.

### First-feature milestone verification, 2026-09-17

- Source documentation acceptance expanded to seven checks; first run failed because the new guides were missing, then passed after implementation. Covers both language guides, real internal paths, startup checkpoints, educational scope, translation limitations and the precise `generate:check` boundary.
- `PATH="/opt/homebrew/lib/node_modules/corepack/shims:$PATH" bash scripts/test-first-feature.sh web`: fresh packed React application passed doctor and locked setup; status accurately reported unknown runtime. Equipment creation passed backend build/OpenAPI, generated architecture/unit tests, frontend generation/typecheck/tests/build and module doctor. The real PostgreSQL isolation test executed and passed (1 passed, 0 skipped), including Equipment in the generated enabled catalog. Assistant-contract freshness passed.
- The same harness in `backend` mode generated genuine `--ui none` output and passed doctor/setup, Equipment creation/build/generated tests/module doctor and real PostgreSQL isolation (1 passed, 0 skipped). No web host was present. Both modes' explicit manifest/registry/field/permission/transport/RLS shape assertions passed; the assertions added during testing were also executed independently against both generated applications.
- Scoped PATH selected an already installed Corepack shim only for test subprocesses; no laptop-global tools were modified. Initial harness failure on macOS Bash's empty-array/nounset behavior was fixed before successful acceptance runs.
- Final `TRYKATCH_KEEP_SKILL_WORKSPACE=true bash scripts/test-development-skills.sh` passed all seven checks for renamed React, backend-only via CLI and declined-script output. All real generation logs print the first-feature guide path. The declined existing Git action retains expected exit 105 and creates no Git directory.
- Existing `ApplicationDevelopmentTests` passed all 14 cases without skips, including accurate unknown runtime and health-validation boundaries.
- Documentation site built 29 pages with zero errors/warnings/hints. Change classification, shell syntax and diff whitespace checks passed. YAML parsing confirmed the new backend/web qualification matrix is required by the aggregate gate; remote GitHub Actions execution is not claimed.
- Local task review checked actual generator output, generated field contracts, catalog integration and test bodies. Corrected walkthrough wording: business labels are not automatically translated, and `generate:check` checks the assistant contract, not client drift. No running product module, assistant/runtime, secret or authorization change was made. Worktree audit after fetch/prune found no safely removable merged secondary worktrees; unrelated dirty/unmerged work remains preserved.

Artifacts: first-feature workspaces `trykatch-first-feature.yEyrB4` (React) and `trykatch-first-feature.IOPF6y` (backend); latest instruction/package workspace `trykatch-skills.vhXtjt`. Implemented and automated checks verified locally, not published. Live fresh-AppHost startup, Equipment HTTP/browser CRUD/permission/language checks and observation of an unfamiliar developer are documented but not run. They remain manual acceptance gaps, not completed checks. No hosted-model calls, global installs, pushes, merges or deployment occurred.

### Astra review finding: backend-only HTTP onboarding, 2026-09-17

Addressed the P2 documentation finding: backend-only output has no installed generated API client, and newcomers lacked a concrete authenticated workspace sequence. Added EN/FR `docs/backend-only-http*.md`, linked from both first-feature startup/live checkpoints and the backend README. The README now identifies the local Development Owner. The recipe explicitly requires separately installed curl/jq and a dedicated Bash shell; uses the actual trusted localhost HTTPS API origin; keeps cookies in one private temporary file; obtains anonymous antiforgery for login, refreshes it after authentication, discovers membership and selects the protected workspace; checks the current workspace/Projects; exercises generated Equipment with invariant decimal strings and current version GUIDs; archives the disposable record and logs out. It does not create an auth/token endpoint, alter server authorization or claim the Owner test establishes other memberships' permissions.

Verification for this fix:

- Added a source acceptance case before the missing guides existed; it failed, then passed. All eight source cases pass, including internal links, generated namespace paths, explicit auth/workspace sequence, token refresh order, both recipes' Bash syntax and exact EN/FR command parity. Change-classification checks select documentation and packed-template verification for the new guides. Whitespace checks pass.
- Final `TRYKATCH_KEEP_SKILL_WORKSPACE=true bash scripts/test-development-skills.sh` passed all eight cases in packed, renamed React, backend-only via the source CLI, and scripts-declined output. The existing declined Git action retains expected exit 105, with no Git directory. Final packed acceptance artifacts are retained in `trykatch-skills.YV77Tn` (earlier intermediate run `trykatch-skills.phmHmm`).
- Started the previously qualified disposable backend application in `trykatch-first-feature.IOPF6y`, with test-only launch ports and distinct PostgreSQL/object-storage/collector volume names. API HTTPS port 37268 and dashboard port 37129 are separate from the user's active app. AppHost applied the Equipment migration, and `/health/live` plus `/health/ready` returned 200 with normal certificate validation.
- Executed the actual Bash code blocks from both language guides against that API, then executed the English blocks from the final packed backend guide. All final sequences passed: cookie login, identity-bound antiforgery refresh, actual membership/workspace selection, current workspace/Projects, Equipment creation/readback/update/readback/archive/recoverable listing/restore/readback/final archive, and logout. Exit traps remove only the private temporary cookie file. Initial live checks caught an incorrect Projects array assertion and lowercase Equipment lifecycle names; corrected these to the actual paged `items` response and `Active`/`Archived` wire values before the successful reruns. Static documentation checks alone did not establish the live pass.

This closes the concrete backend-only authentication-guidance finding locally. No running product source, secrets, in-app AI Help, deployment settings or user database was changed; no hosted inference call, push, merge or release occurred. The isolated test data remains archived/recoverable in retained test storage. Test-only AppHost is shut down after acceptance. Browser/React live CRUD, separate denied/cross-organization HTTP accounts, new-developer observation and remote CI are still not established by this recipe. Previous PostgreSQL qualification evidence remains separate; no additional RLS pass is claimed for this documentation fix.

### Release candidate 0.1.0-preview.25, 2026-09-17

The user authorized local qualification followed by release if passing, including synchronized names/version documentation. The dedicated conventional release branch includes the committed developer-experience prerequisites and scoped onboarding/facts/first-feature work, not the unrelated uncommitted in-app AI Help implementation. Package IDs remain `Trykatch.Templates` and `Trykatch.Cli`; template identity and short name remain unchanged. Both package versions, canonical RELEASE_VERSION, current English/French installation commands, README, landing page, installer examples/tests and readiness baseline agree on preview.25. Historical evidence is not rewritten.

Verified independently against the clean candidate:

- Release-version and CLI documentation contracts pass; all eight source instruction checks, change classification and whitespace checks pass.
- The focused ApplicationDevelopment/ModuleWorkspace/TemplatePackageInstaller test selection passes 64 tests, no skips.
- The complete source unit project subsequently passes all 281 tests with zero failures/skips. Both actual preview.25 NuGet packages pack successfully, separately from the unique CI-version CLI used by the isolated acceptance harnesses.
- Documentation site builds all 29 pages with zero errors/warnings/hints.
- Fresh preview.25 packed backend (`trykatch-first-feature.LpVrLz`) and React (`trykatch-first-feature.AE6YTG`) applications both pass doctor, locked setup, honest unknown-runtime status, Equipment creation, generated module unit/architecture checks, installed facts and module doctor. React also passes client generation, typecheck, frontend tests, production build and assistant-contract freshness. Each genuine PostgreSQL catalog isolation run passes exactly one executed test with zero skips.
- Started the fresh preview.25 backend AppHost using isolated ports and newly named test volumes. Once startup completed, live/ready returned HTTP 200 with normal TLS verification. Executed the Bash recipe from that generated guide against its API: login, refreshed antiforgery, discovered membership, protected workspace selection, current workspace/Projects, Equipment persisted create/read/update/archive/restore and logout all pass. Disposable record remains archived; temporary cookies are removed. The test AppHost was stopped without changing the user's app.

At this recording point remote PR/main promotion and trusted NuGet publication are pending. Local evidence does not imply a completed release or establish the previously stated browser/usability/other-membership HTTP gaps. Release proceeds only after the required remote checks pass; version tags must point to a commit on main.
