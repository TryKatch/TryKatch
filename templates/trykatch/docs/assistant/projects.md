# Projects module

## Using Projects

Open Projects to view active projects in your selected organization. Viewing records requires projects.read. Creating or changing projects requires projects.manage. Your role's actual permissions decide which controls and operations are available; this guide does not grant access. A project has a name and description and tracks creator and timestamps. The Projects page and API operate in the current organization, not across all organizations.

Projects use recoverable lifecycle behavior: archive removes a project from normal active lists; restore brings an archived record back; reasoned deletion is a separate operation. Use Archive for recovery where your permissions allow it. Do not confuse an empty active list with a count of all archived or deleted records.

## Developer entrypoints

ProjectUseCases in the module's Application project orchestrates behavior and authorization. Project in Domain owns domain state. ProjectsEndpoints in Presentation declares HTTP operations. ProjectStore, ProjectsModelContributor and ProjectsModule in Infrastructure own persistence, EF mappings and registration. The module's Web package owns ProjectsPage and declares its frontend module entrypoint.

The projects.list.after-table extension point permits a web contribution after the project table. Use that published seam; do not import another module's private component or store. Run module facts projects through the local module tool to inspect installed declarations and exact entrypoint paths before changing code. The facts command is read-only.

## AI access and limits

The assistant can use explicitly registered read-only project list/detail adapters only when projects.read is granted. List results are bounded and may have more matches. The assistant cannot create, update, archive, delete or restore a project. To change a project, use the normal authorized UI/API workflow. This guide describes the shipped module, not an inspection of custom source modifications in your application.
