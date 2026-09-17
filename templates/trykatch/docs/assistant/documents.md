# Documents module

## Using Documents

Open Documents for files in your selected organization. documents.read permits viewing metadata; documents.manage controls uploads and changes. A document may be associated with a project according to the module's normal workflow. The installed catalog declares Documents' dependency on Projects; do not assume Documents is present when it is disabled or uninstalled.

Uploaded files are stored privately through the host's object-storage interface, not public URLs. Metadata, object keys and file content have different security implications. The normal download workflow authorizes access and scopes the record; a caller-provided record identifier must not escape the current organization.

## Developer boundaries

Documents is an independently packaged business module with its own Domain, Application, IntegrationEvents, Presentation, Infrastructure and optional Web surface. DocumentsModule is its registration entrypoint. Use the host's IOrganizationModuleData, IModulePermissionAuthorizer and IObjectStorage seams; do not reach into Projects' private application/store code. The documents.projects-presence web extension targets projects.list.after-table.

Use module facts documents to inspect the exact installed manifest, package/source ownership, entrypoints, permissions and extension declarations. A declared permission or assistant descriptor is metadata, not proof a runtime adapter exists. Package upgrades do not authorize overwriting application-owned customizations.

## What AI can see

The assistant's document list adapter returns bounded metadata only. It does not upload files, fetch file contents or send private object-storage keys to the AI provider. It cannot explain the contents of a PDF or document from its filename alone. A document-update descriptor marked as requiring confirmation does not enable writes: the running v1 assistant explicitly rejects mutating tools. Use the normal Documents workflow for changes.
