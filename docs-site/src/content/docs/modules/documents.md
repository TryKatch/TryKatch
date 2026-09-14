---
title: Document uploads
description: Understand the reference Documents module, local MinIO resource, and production storage boundary.
---

Projects and Documents are both enabled in the starter, but they demonstrate different module shapes. Projects is ordinary organization-owned CRUD. Documents is a full-stack file workflow: the browser uploads a file to the authorized API, PostgreSQL stores searchable metadata, and private object storage holds the bytes.

## Local development

`trykatch start` starts MinIO with Aspire when the application was generated with the default `--storage true` option. Aspire binds MinIO's dynamic development ports to loopback and does not advertise them as external endpoints; the browser never receives its credentials.

1. Start Docker Desktop or another Aspire-supported container runtime.
2. Run `trykatch start` from the generated application root.
3. Sign in, open **Documents**, and choose **Upload document**.
4. Upload a non-empty PDF, Office document, text, CSV, JPEG, PNG, or WebP file up to 25 MB.
5. Open the row action and use **Download** to verify the authorized stream.

`--storage false` keeps the same host contract but uses local filesystem storage. That mode is useful for simple development only and is not appropriate for read-only or horizontally scaled production containers.

## Security and isolation

The API checks `documents.manage` for uploads and metadata changes and `documents.read` for listing and downloading. Mutations also require an antiforgery token. The server creates an opaque key shaped like `organizations/{organization-id}/documents/{document-id}`; user-supplied names never become storage paths. That key and all storage credentials stay out of API responses.

The `app.documents` table uses the same automatic organization filter and forced PostgreSQL RLS policy as other organization data. A user must first be able to read the organization-scoped metadata record before the API asks object storage for its bytes. File names are normalized, types are allowlisted, request size is bounded, and every upload records a SHA-256 digest. If metadata persistence fails after an object upload, the application attempts compensating object deletion.

Archive and deletion-request operations retain file bytes because the central recovery workflow can restore the record. Permanent storage disposal belongs in a separate retention-policy worker with its own authorization and audit contract.

## Production

Documents depends on the provider-neutral `IObjectStorage` contract. MinIO is only the Aspire development adapter; the module's domain and application layers do not reference MinIO or an S3 SDK. The included infrastructure adapter works with S3-compatible endpoints, and another provider can replace it by implementing the same contract without changing the Documents module.

The pinned community MinIO image is a local-development convenience. Current upstream MinIO security advisories direct production users to patched supported releases, so Trykatch does not publish the local MinIO container as a production storage recommendation.

Configure the production API with `Storage__ServiceUrl`, `Storage__AccessKey`, `Storage__SecretKey`, `Storage__Bucket`, and `Storage__Region`. Pre-create the private bucket, set `Storage__CreateBucket` to `false`, grant the application identity access only to that bucket, use TLS, enable encryption and versioning according to your data policy, and monitor capacity and failed object operations.
