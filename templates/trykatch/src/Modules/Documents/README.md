# Trykatch Documents module

Reference independently packaged object-storage module. It demonstrates authorized multipart upload, opaque organization-scoped object keys, an S3-compatible host storage seam, forced PostgreSQL RLS for metadata, streamed API downloads, recoverable lifecycle operations, audit/outbox events, and a React contribution. The browser never receives storage credentials or raw object keys.

## Upload and classify

Start the generated application with `trykatch start`, sign in, and open Documents → Upload document. Choose a supported file up to 25 MB, confirm its title, select Invoice, Contract, Certificate, Report, or Other, and add optional notes. The dialog displays file size, validation errors and an indeterminate upload status. Metadata can be edited without replacing the stored file.

Business classification is separate from MIME format. Multipart uploads and JSON metadata updates accept `documentType`: `invoice`, `contract`, `certificate`, `report`, or `other`. Unknown values are rejected. The additive `202609151400_document_type` migration backfills existing documents as `other`, retains forced RLS, and adds a database allowlist constraint. Omitted classification defaults to `other` on upload and preserves the current type on update for older clients. Run the application migrator before starting an upgraded API; Aspire coordinates this automatically.
