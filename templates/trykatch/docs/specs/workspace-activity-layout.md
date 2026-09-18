# Workspace activity card alignment

The 2026-09-17 UAT screenshot shows Recent activity's heading and row markers against the surface border. Preserve API-backed audit content, translations, ordering and the View all link; fix spacing without adding a table or inventing records.

## Acceptance

- The card owns its layout outside `content-grid`: header and rows use a consistent 16px horizontal inset.
- Activity titles and actor/time details wrap inside the card, including long unbroken names.
- The status dot does not shrink and aligns with the first text line.
- Header/link layout and document overflow remain correct at 320, 768 and 1440px, in English/French and light/dark themes.
- Empty/loading/error states and audit navigation remain unchanged.

## Diagnosis and verification

The real generated application's browser check failed before the fix: heading inset 1px, expected at least 16px. Computed styles showed no surface padding and a parent `.page`, while the only old card-spacing rule depended on `.content-grid > .surface`. No document overflow or cascade reset explained the symptom. The card had lost that wrapper, so it lost the inherited spacing contract.

Two component/CSS-contract regressions cover the dedicated surface class, scoped header/row spacing, wrapping and nonshrinking dot. They failed before correction and pass afterwards. The browser heading check now reports 17px (16px padding plus 1px border). CSS-contract tests do not replace real-browser responsive/row verification; that acceptance is recorded in the release delivery report.
