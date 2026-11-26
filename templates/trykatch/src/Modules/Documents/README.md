# Trykatch Documents module

Reference independently packaged object-storage module. It demonstrates authorized multipart upload, opaque organization-scoped object keys, an S3-compatible host storage seam, forced PostgreSQL RLS for metadata, streamed API downloads, recoverable lifecycle operations, audit/outbox events, and a React contribution. The browser never receives storage credentials or raw object keys.

## Upload and classify

Start the generated application with `trykatch start`, sign in, and open Documents → Upload document. Choose a supported file up to 25 MB, confirm its title, select Invoice, Contract, Certificate, Report, or Other, and add optional notes. The dialog displays file size, validation errors and an indeterminate upload status. Metadata can be edited without replacing the stored file.

Business classification is separate from MIME format. Multipart uploads and JSON metadata updates accept `documentType`: `invoice`, `contract`, `certificate`, `report`, or `other`. Unknown values are rejected. The additive `202609151400_document_type` migration backfills existing documents as `other`, retains forced RLS, and adds a database allowlist constraint. Omitted classification defaults to `other` on upload and preserves the current type on update for older clients. Run the application migrator before starting an upgraded API; Aspire coordinates this automatically.

The upload dialog retains an operation UUID when retrying an unchanged file and normalized metadata after an uncertain response. Changing the file or metadata, closing the dialog, or changing organization starts a new operation. Multipart callers may supply one non-empty `operationId` UUID; older callers that omit it receive a fresh operation for every request. The server derives the organization, actor, content checksum and metadata fingerprint itself. A retry must still have current management access and match the original actor, membership and upload identity.

Documents declares the versioned `documents.upload` policy on `documents.storage-bytes`. The host durably admits capacity before a conditional object write and commits metadata, audit, event and usage together. A completed retry returns the document's current metadata, including its Archived state, without repeating those effects. A pending-deletion or missing result is not recreated. Archive, restore and deletion requests retain their logical storage usage; they do not confirm permanent removal.

Finite capacity exhaustion returns HTTP 409 with `quota_exhausted`; unavailable storage or coordination returns HTTP 503. Retry an unchanged uncertain upload with the same operation ID. Unknown outcomes retain their durable reservation for reconciliation. Enforcement is disabled by default while usage is still recorded; operators must complete the initial inventory before enabling finite limits. See [ADR 0022](../../../docs/adr/0022-organization-quotas-and-upload-fencing.md) for the recovery contract.
