# Module package trust and installation

Package modules are trusted code, not sandboxed extensions. Workspace modules remain reviewed source. A publisher name or a registry signature alone does not authorize a package installation.

## Publisher enrollment

An operator must enroll the publisher in `trustedPublishers` and add its independently obtained identity under `publisherTrust` in `trykatch.modules.json`:

```json
{
  "publisherTrust": {
    "publisher-id": {
      "nugetSignerSha256": ["<64 hexadecimal characters from the approved signer certificate>"],
      "attestationPublicKey": "-----BEGIN PUBLIC KEY-----\n<enrolled RSA public key>\n-----END PUBLIC KEY-----",
      "builderId": "https://publisher.example/approved-builder",
      "maximumAttestationAgeDays": 7
    }
  }
}
```

The attestation key must be RSA with at least 3072 bits. The maximum accepted attestation age is configurable from 1 to 30 days. No production publisher key is invented or implicitly trusted by the template. NuGet configuration must still require signatures and define trusted signers and source mappings. Do not enroll a key or signer fingerprint merely because the candidate manifest supplies it.

## Release evidence

Both `.nupkg` and frontend `.tgz` artifacts must be supplied beside the reviewed manifest with exact ID, version, relative `packageFile`, and SHA-256. Archive metadata must match those identities. Backend signing is verified with `dotnet nuget verify --all --certificate-fingerprint` against the enrolled fingerprints.

`distribution.supplyChain` requires `provenanceFile`, `provenanceSha256`, `provenanceSignatureFile`, `sbomFile`, and `sbomSha256`. The signature file contains a base64 RSA-PSS/SHA-256 detached signature over the exact provenance file bytes.

Provenance is an in-toto Statement v1 with SLSA provenance v1 predicate. Its subjects must be exactly the backend package URL, the frontend package URL when present, and a subject named `sbom`; each contains the corresponding SHA-256. Scoped npm package URLs encode `@` as `%40`. `runDetails.builder.id` must equal the enrolled builder, and `runDetails.metadata.finishedOn` must satisfy the age policy. `buildDefinition.buildType` is required. The signed `buildDefinition.externalParameters.securityScan` must contain `status: "passed"`, `high: 0`, and `critical: 0`. This is trust in an enrolled builder's scan attestation, not an independent live vulnerability scan by the CLI.

The signed SBOM must be SPDX 2.3 with document identity, namespace, CC0-1.0 data license, creation information, package URLs and SHA-256 checksums matching the artifacts, and document `DESCRIBES` relationships. A matching hash of an arbitrary file is insufficient.

## Transaction boundary

Verification snapshots the artifact bytes before changing package references, generated registries, or locks. Install/upgrade transactions are serialized. The verified backend is copied into a content-addressed local feed with an exclusive exact NuGet source mapping. Restore uses a fresh private package cache, and its resulting archive is checked against the reviewed digest. The frontend dependency is a local tarball reference; the package version is checked inside the archive and pnpm records its integrity in the lock file.

An install failure restores references, NuGet configuration, generated registries, package locks, and the standard NuGet-generated project artifacts. Verified artifacts are application-owned inputs and should be retained with the workspace. Future locked restores use the same local sources; do not replace them with registry references without repeating verification.

## Data access profiles

Organization resources live in `app` and require the host's forced-RLS equality policy. Non-organization resources must explicitly select a closed access profile: `platform-only` in `platform`, `identity-only` in `identity`, `global-read-only` in `reference`, or `host-only` in `infrastructure`. Only global reference reads are granted to the organization role; the other non-organization profiles cannot acquire organization-runtime CRUD access.

The migrator maintains `platform.module_data_resources`, a read-only metadata catalog for runtime roles. Generated installed-resource declarations contain data only; disabled modules are not constructed or registered. Existing schema declarations survive disable/unregister and remain subject to inspection, while new undeclared tables and changed ownership fail closed.
