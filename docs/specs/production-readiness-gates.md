# Production-readiness engineering gates

## Outcome and scope

Continue the [enterprise foundation hardening plan](../plans/enterprise-foundation-hardening-plan.md), not only reconcile its checklist. This implementation record covers the immediate request-logging defects in 09, generated SDK/workflow controls in 10 and artifact/contract delivery controls in 11. The [evidence ledger](../enterprise-foundation-evidence.md) remains the complete 16-item backlog; this slice does not declare unrelated criteria complete.

No production data, repository permissions, NuGet policies, public package versions or signing keys are changed by these local implementation tests. Actual recovery/capacity commitments, manual accessibility and independent approval require separate evidence.

## Decisions and ownership

- Request completion belongs to `Trykatch.ServiceDefaults`; the existing privacy sink remains the allowlisted export boundary.
- HTTP exceptions propagate unchanged. Diagnostic failure/abort status is not an instruction to change the response or silently retry a request. `ResponseStarted` distinguishes already-started responses; client abort uses diagnostic 499.
- Successful operational probes remain suppressed. Their failures and aborts remain visible.
- Route values come from the actual `RouteEndpoint`, never raw URLs or query strings. Methods, outcomes and diagnostic values are bounded; exception messages/bodies are not emitted.
- Release orchestration belongs to the root workflow/scripts. Existing harnesses keep standalone packing for developer use; prepared candidates require a digest/identity manifest and never repack.
- Tool installation uses an isolated package cache and a cleared, local-only feed configuration so an existing public/cache version cannot substitute for the candidate CLI.
- Publication consumes a specific qualified workflow artifact. It verifies package digests, sizes, version, commit, workflow run and the approved successful source CI run before obtaining a temporary NuGet credential. No duplicate-skip result is interpreted as successful publication of these bytes.
- Preview jobs retain the existing environment-free publishing identity. Stable publishing is separate, requires completed hardening criteria and a configured reviewer-protected environment, and waits for that approval. An absent environment does not silently allow stable publishing.
- Backend-only input changes also select the web contract check. That job builds OpenAPI and generates/compares clients in the same checkout.

## Acceptance and verification

| Criterion | Test layer / evidence | Current status |
| --- | --- | --- |
| Matched/unmatched routes and 200/401/403/404 outcomes produce safe completion data. | `SafeRequestLoggingTests`, direct real middleware with framework endpoints. | Verified locally. |
| Exceptions propagate unchanged; aborted and unrelated cancellation paths are distinguishable and complete once. | Same regression suite; baseline failed before the middleware fix. | Verified locally. |
| Successful health probes are suppressed; unsuccessful ones remain visible; values are bounded. | Same regression suite. | Verified locally. |
| Completion survives the actual privacy sanitizer without exporting planted path/exception secrets. | Actual middleware event passed through `SafeTelemetrySink.Sanitize`. | Verified locally. |
| Candidate/altered/missing/extra/symlinked/wrong-identity packages cannot pass publication verification. | `node --test scripts/ci/test-release-artifacts.mjs`; harmless isolated fixtures, no public push. | Verified locally. |
| Prepared candidate selection does not repack. | Helper exercised with a failing `dotnet` stub; real prepared candidates installed in isolated template hives/tool caches. | Verified locally; backend blueprint PostgreSQL acceptance passed (2 tests, zero skips). |
| Publication depends on qualification, downloads its artifact, verifies before credentials and never rebuilds; previews retain their existing identity. | Workflow contract regression plus actionlint across root and generated workflows. CI also tests candidate upload/download without publishing. | Regression and workflow lint verified; actual GitHub transfer pending integration. |
| API-only changes select the contract job and build API before client regeneration in one checkout. | `bash scripts/ci/test-detect-changes.sh`, fresh API build, `corepack pnpm generate`, `generate:check` and zero generated diff. | Verified locally. |
| Generated SDK/action/cache policies are reproducible before first restore. | `node --test scripts/ci/test-generated-reproducibility.mjs`; three criteria failed before the fix. SDK selection matches source CI, generated CI and existing digest-pinned 10.0.301 container SDK. | Four policy regressions verified; cross-platform/manual IDE qualification remains open. |
| Candidate template and CLI work in real isolated generated projects. | Prepared-package template matrix and backend/React blueprints, installed through private hives and local-only tool feeds. | Matrix and both blueprint modes verified locally; each blueprint passed 2 real PostgreSQL tests, zero skips. Generated production-container/browser rerun pending CI. |
| Actual GitHub artifact transfer, protected-environment approval and NuGet token exchange work for the revised release pipeline. | Main/release workflow evidence after integration; no fake public package push. | Not run; requires integration and a separately approved release. |

The focused logging/telemetry run passed **20 tests, zero skips**. Source unit and host architecture suites passed **282** and **12** tests respectively, zero skips. Release-artifact/transport/helper and generated-policy regressions passed **12 tests**. The source API's `--locked-mode` restore also passed; this does not qualify every AppHost/OS graph.

Local prepared-package manifests use run/source-CI IDs `0` and remain **candidate**, not qualified release attestations. Their package version is the current preview value for isolated testing; they are working-tree builds, not the public preview.25 package. The initial template harness run stopped on pnpm's non-interactive dependency replacement; the harness now explicitly sets `CI=true`, matching GitHub. A blueprint run was interrupted by editing its running shell script; static syntax and a frozen-input rerun passed. Neither interrupted run is counted as a pass.

Retain command results and update pending rows after executing them. Existing preview.25 CI is historical evidence for its unchanged controls, not qualification of these unreleased runtime/workflow changes.

## Clean repository CI — 2026-09-17

[Run 35234520274](https://github.com/TryKatch/TryKatch/actions/runs/35234520274) passed all **18 jobs** for PR head `bc05408e5bfd02f4d9dfcd83948f498089a62b37`, tested as merge commit `8e45dcf176a84ea49e4b13fb72341b8fef21ecaf`. It passed **282 source unit tests and 247 integration tests**, zero failures/skips, the generated production-container/browser rerun, both blueprints, prepared-package matrix and actual GitHub candidate transfer/negative-publication check. This supersedes the local record's pending PR-CI rows above, not its unexecuted tag-release, stable approval or deployment acceptance rows. Full details and candidate digests are retained in [the qualification comment](https://github.com/TryKatch/TryKatch/pull/109#issuecomment-5716351612). Publication of preview.26 remains conditional on its own successful main/tag workflows.
