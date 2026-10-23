# ADR 0019: Local S3 development service

Status: Accepted; development and CI scope only.
Date: 2026-10-24.

## Context

Documents already stores bytes through `IObjectStorage`, implemented by the host's AWS SDK S3 adapter. Production Compose takes an external S3 endpoint and credentials. AppHost development and generated-application qualification previously started a pinned MinIO container.

That exact MinIO artifact no longer permits anonymous pulls. Rebuilding the old server would also preserve known security defects: upstream [GHSA-hv4r-mvr4-25vw](https://github.com/minio/minio/security/advisories/GHSA-hv4r-mvr4-25vw) and [GHSA-9c4q-hq6p-c237](https://github.com/minio/minio/security/advisories/GHSA-9c4q-hq6p-c237) identify authentication bypass affecting all open-source releases and describe fixes in the commercial product. A registry mirror or private pull credential would not resolve those defects.

## Decision

Use the Apache-2.0 [RustFS 1.0.3 release](https://github.com/rustfs/rustfs/releases/tag/1.0.3) for the local development service and disposable CI storage. Pin its public multiarchitecture image to `rustfs/rustfs:1.0.3@sha256:9ca9559533b537d10482fe923288b676ad08f92a7069d9a17327fbae3cab688b`. The reviewed [vendor advisories](https://github.com/rustfs/rustfs/security/advisories) identify affected versions through 1.0.1, with fixes available in 1.0.2; this does not establish that the whole container has no vulnerabilities or certify it for production.

Preserve the existing `IObjectStorage` contract, AWS SDK configuration, organization object keys and external production S3 settings. Modules do not acquire a RustFS dependency. AppHost run mode registers `object-storage`, checks `/health/ready`, and supplies its endpoint and local credentials to the existing adapter. Existing Aspire secret parameter names remain compatible. Publish mode omits the local container and requires external endpoint, secret access key, secret key and bucket parameters, with `us-east-1` as the default region and automatic bucket creation disabled. No production provider or commercial license is selected.

Use a new `trykatch-app-slug-object-storage-data` volume. Never mount an existing MinIO data directory into RustFS, implicitly migrate objects, or delete the old volume. New generated products receive this default; existing generated products require an explicit upgrade and verified object copy before repointing their S3 configuration. The [operations guide](../operations/local-object-storage.md) describes this boundary.

## Qualification and limits

The real S3 adapter must create a bucket, PUT and byte-exact GET an object across a container restart, DELETE it and observe its absence. Wrong credentials and anonymous reads must be rejected by the actual server. Generated application qualification must exercise its existing Documents flow against the pinned service; source assertions alone are insufficient. Verify the image on supported amd64 and arm64 development/CI platforms and verify that AppHost publishing contains only external S3 parameters.

This is a single-node development convenience. It does not qualify production availability, backup/restore, large multipart transfers, encryption, or arbitrary S3 extensions. Production service selection, policies, TLS, credential rotation and migration remain deployment decisions. No live data or deployment was changed. Concrete checks and remaining limitations are recorded in the [E4 evidence](../specs/evidence/parity-execution-e4.md).
