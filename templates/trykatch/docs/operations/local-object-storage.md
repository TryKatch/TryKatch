# Local and published object storage

The optional Documents capability stores file bytes through the host's S3-compatible `IObjectStorage` adapter. It does not depend on a specific storage provider in module code. The checked-in Compose deployment continues to use externally managed S3-compatible storage; AppHost run mode supplies a local RustFS service for development.

## AppHost run mode

When `--storage` is enabled, `dotnet run --project src/API/<App>.AppHost` starts the API and a local RustFS 1.0.3 container. The image is pinned to `rustfs/rustfs:1.0.3@sha256:9ca9559533b537d10482fe923288b676ad08f92a7069d9a17327fbae3cab688b`. RustFS exposes S3 and console endpoints through AppHost; the API receives the S3 endpoint and uses the development bucket `trykatch-documents` with automatic bucket creation enabled.

AppHost retains the existing local secret parameter names `minio-access-key` and `minio-secret-key` so existing Aspire secret values remain usable; it passes those values to RustFS as `RUSTFS_ACCESS_KEY` and `RUSTFS_SECRET_KEY`. Keep these local development secrets private and use unique credentials for any RustFS instance reachable beyond the developer's machine. RustFS persists data in the new `trykatch-app-slug-object-storage-data` volume. The former `trykatch-app-slug-minio-data` volume is not mounted, renamed, copied, or removed by this change.

The pinned image and the local setup are development defaults, not a production readiness or high-availability claim. See the [RustFS container documentation](https://docs.rustfs.com/en/installation/container/docker) and [RustFS 1.0.3 release](https://github.com/rustfs/rustfs/releases/tag/1.0.3).

## AppHost publish mode

AppHost publish mode does not start the local RustFS container. A storage-enabled publish requires these external parameters:

| Parameter | Requirement |
| --- | --- |
| `storage-service-url` | Required S3-compatible service endpoint |
| `storage-access-key` | Required secret; use a bucket-scoped key |
| `storage-secret-key` | Required secret; use a bucket-scoped secret |
| `storage-bucket` | Required existing bucket |
| `storage-region` | Defaults to `us-east-1` |

The published API has automatic bucket creation disabled. Supply these values through the deployment's secret/configuration mechanism; do not commit or publish credentials. This template does not configure a production AppHost publisher or claim that publishing AppHost is equivalent to the maintained Compose deployment. The external S3-compatible endpoint, bucket policy, TLS, backup, availability, and credential lifecycle remain deployment-owned.

## Compose and generated applications

See [conditional object storage](conditional-object-storage.md) for the managed upload namespace, permanent cancellation sentinels, captured destination fingerprints and local filesystem qualification requirements.

The maintained production Compose configuration remains external-provider based. Set `TRYKATCH_STORAGE_SERVICE_URL`, `TRYKATCH_STORAGE_ACCESS_KEY`, `TRYKATCH_STORAGE_SECRET_KEY`, `TRYKATCH_STORAGE_BUCKET`, and `TRYKATCH_STORAGE_REGION` as described in `.env.example`. Compose does not start RustFS and does not create the configured bucket. Protect `.env` and provide bucket-scoped credentials through the deployment's secret handling process. The application continues to use the existing AWS SDK S3 adapter and `IObjectStorage` contract; no provider-specific application API or object-key format is introduced.

Newly generated products use the RustFS AppHost development service by default when storage is enabled. Existing generated products are not updated automatically when the template changes. A maintainer upgrading an existing generated application must plan any data move separately: copy and verify stored objects in the target service, then verify and repoint that application's `Storage:ServiceUrl` before changing existing document records or routing requests to the new store. Keep the old MinIO volume until the copied data and application behavior have been verified and its owner explicitly approves disposal. This template update performs no live data migration, volume deletion, or credential publication.
