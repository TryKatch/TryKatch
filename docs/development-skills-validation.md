# Development skill validation

The shipped bundle lives in `templates/trykatch/.agents/skills/`, with its router in `AGENTS.md` and focused references in `docs/development/` inside the template. Keep technical contracts in those maintained guides and source references rather than copying them into each skill.

## Packaging checks

From the repository root:

```bash
bash scripts/test-development-skills.sh
```

This packs the real NuGet template into a private temporary directory, installs it into an isolated template hive, and creates both `Skill.FullStack` and `Skill.Backend-Only`. It checks all five skill names and frontmatter, router reachability, Markdown reference resolution inside the application, and namespace-replaced solution/tool/source paths. It also requires the user-help API/runtime and six explicit embedded guides; React output must include both the floating chat launcher and account-menu entry with continuation support. Backend-only output must retain the server runtime without a web workspace. The temporary workspace is removed on success and retained on failure; set `TRYKATCH_KEEP_SKILL_WORKSPACE=true` to retain successful output for a behavioral trial.

The same checks run against every generated application in `scripts/test-template.sh`. CI classifies skill, router, guide, and validator changes as packaging changes. These checks prove distribution and reference integrity, not how well a model will follow the instructions.

For an existing generated application, use:

```bash
node scripts/test-development-skills.mjs /path/to/application Application.Namespace
```

The validator belongs to this repository; the path argument identifies the generated application being checked.

## Behavioral trial

Use a disposable generated application with an independent coding-agent session. Give it the skill file and a realistic request, without the intended implementation. For example:

> Read `.agents/skills/trykatch-build-module/SKILL.md`. Build a shipment reception workflow with a React screen using the shipped blueprint. Receptions start Draft, can be submitted, accepted after documents are verified, or rejected with a reason. Reject acceptance when received weight exceeds dispatched weight. Implement and verify the feature.

Then exercise an existing-module change:

> Read `.agents/skills/trykatch-extend-module/SKILL.md`. Require rejection reasons to have at least 20 characters; keep the 500-character maximum and preserve the module's existing behavior. Update the specification and tests, review the change, and verify it.

Inspect the resulting artifacts and actual command output. Look for a covering spec, correct generator selection, custom-rule tests, preservation of edited module source, relevant API/PostgreSQL evidence, and an honest report of browser checks. Review the task's changed files even when a freshly initialized repository has no initial commit. Do not count generated unit tests alone as proof of HTTP authorization or RLS.

Also try a specification-only or review-only request and a backend-only application when changing scope or frontend routing instructions. Those requests must stay within their requested scope and use only the available application surfaces. Correct the bundle only for demonstrated gaps; avoid accumulating rules for hypothetical failures.

No live deployment, external PR, or global agent configuration is needed for these trials. Keep feature artifacts outside the template repository, and preserve diagnostics until the result has been assessed.

## Recorded trial: 2026-09-16

The initial bundle was exercised in an independently operated `Skill.FullStack` application generated from the packed template. The agent implemented the shipment request above, then extended the existing module to require 20–500 character rejection reasons. All five skills were used across specification, generation, extension, review, and verification.

- Packed React and backend-only applications passed all four bundle checks each, including namespace substitution and references. The backend-only local CLI passed `module doctor` and blueprint validation.
- Full-stack creation completed its backend/OpenAPI, module architecture/unit, generated-client, frontend typecheck/test/build, and doctor checks.
- The extended module passed 17 unit tests, the host passed 12 architecture tests, and two focused integration tests passed against real PostgreSQL with no skips. The HTTP fixture included weight boundaries and 19/20/500/501-character rejection reasons; the separate runtime-role test covered declared organization relations.
- A browser trial created an overweight reception, submitted it, observed Accept omitted and Edit disabled, saw a validation error for a 19-character rejection reason, and completed rejection with a valid reason. This was one desktop workflow, not exhaustive responsive, permission-role, or conflict UI coverage.
- The agent stopped its application processes/containers and closed its browser tab after the trial.

The trial exposed an instruction gap: the shipped HTTP acceptance fixture lacked a discoverable copy/adapt/run recipe. That recipe is now in the bundled verification guide, and the final package/reference checks were rerun successfully.

The review also identified a pre-existing generator limitation: [BlueprintStore.cs.tpl](../templates/trykatch/tools/Trykatch.ModuleTool/Scaffolding/Templates/BlueprintStore.cs.tpl) materializes the full list with `ToArrayAsync`, and [BlueprintWebIndex.tsx.tpl](../templates/trykatch/tools/Trykatch.ModuleTool/Scaffolding/Templates/BlueprintWebIndex.tsx.tpl) requests that whole list. This does not satisfy the frontend guide's server-pagination requirement for unbounded collections. The skill bundle records the finding; pagination implementation is separate generator work. The trial validates the workflow and its ability to surface gaps, not unrestricted production scalability.
