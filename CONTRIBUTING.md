# Contributing

Use conventional branches and commits, keep the canonical template runnable, and add tests for behavioral changes. Template options must be tested both enabled and disabled.

Contributions must not copy source or assets from commercial boilerplates. Dependencies must be compatible with commercial Apache-2.0 use.

## Release and documentation version policy

`RELEASE_VERSION` is the repository's canonical published package version. Any pull request that changes the CLI or generated template in a way that requires a new package release must update that file and every current-version installation surface in the same change. This includes both NuGet project files, the README, the English and French installation guides, the product landing page, and version-aware tests.

Historical version references in migration guidance and release records must remain accurate. Do not replace those merely to make them look current.

Run the version contract before opening a pull request:

```bash
bash scripts/check-release-version.sh
```

CI runs the same contract on every pull request and branch build. Tagged releases additionally require the tag, such as `v0.1.0-preview.23`, to match `RELEASE_VERSION`; publication fails before packing when they differ.
