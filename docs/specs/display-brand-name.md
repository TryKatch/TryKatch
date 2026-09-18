# Customer-facing display brand

## Originating request

During Mailpit UAT the user saw the technical name `Email.ReleaseProof` in the email sender, subject and body. The agreed direction was to keep dots in .NET namespaces but separate the customer-facing product name, consistently across the app and emails. The user authorized implementation, then requested a review and PR.

## Acceptance criteria

1. A technical project name such as `Kamenta.App` can display `Kamenta` without changing its namespaces or project paths.
2. Template generation exposes a separate display-name option with a default independent of the technical name.
3. Product brand marks, accessible brand labels, authentication/invitation/activation pages, navigation and the browser title use the configured display brand.
4. Invitation and reset email subjects and HTML use the display brand. Local Mailpit's default sender display name follows that brand, while an explicit sender remains operator-owned and takes precedence.
5. Names containing quotes or other punctuation are safely represented, not inserted as executable source.
6. English/French onboarding/configuration documentation describes generation and later overrides. Captured historical email remains unchanged.
7. Regression coverage exercises packed full-stack and backend-only generation and the affected frontend/email behavior.

## Boundaries

No authentication, authorization, tenant isolation or password-reset semantics change. No historical Mailpit email rewrite. No production SMTP2GO delivery claim or public release publication in this PR. Marketing/tutorial material describes the template rather than newly generated business-product behavior.

## Review and verification — 2026-09-17

Parallel standards and behavior reviews used develop commit `bcccb7c6f5dbde299168385dc027f875e1426037` as the baseline. Standards found a macOS-only test temp path and missing package-version coordination; both were corrected and re-reviewed with no remaining findings. Behavior review reported no blocking mismatches.

- Final preview.28 version contract passed; both packages and active install/readiness documentation agree. Historical preview.27 release notes remain intact.
- Packed preview.28 default/backend-only and custom/full-stack generation checks passed with `TMPDIR=/tmp`, including quoted brands and preserved namespaces.
- Final source's focused CLI/application-creation/email unit suite: 40 passed, zero skipped.
- Branding implementation's frontend workspace typecheck, all 74 web tests and production build passed. Browser brand/title checks passed on login, forgot/reset, activation and unavailable-invitation screens, with 320px and 1440px screenshots visually inspected.
- Email-enabled generated preview.27 candidate: 12 email-brand unit tests and 17 real-loopback SMTP integration cases passed. These are retained implementation-level evidence, not claims of preview.28 public-install or complete authentication qualification.
- English/French documentation site build passed with no Astro diagnostics.

The authenticated shells were compiled/code-reviewed, not browser-tested with a genuine login in the branding fixture. Public package replay, production SMTP2GO delivery, and release qualification remain separate gates. The existing UAT accounts and Mailpit inbox were unchanged.
