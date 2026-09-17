# Architecture overview

## Getting started with the application

This foundation provides an organization workspace with an Overview, enabled business modules and role-controlled administration. The shipped examples are Projects for organizing work and Documents for storing related files. Only describe example modules when the enabled-module declarations confirm they are installed; a generated customer application can replace these examples with its own product features.

Start on Overview to see your workspace's summary. Open Projects to find work records, or Documents to find files, when those menus are available. To create a project, open Projects and choose New project; enter its name and description and save. If a management control is unavailable, ask the workspace administrator for the appropriate access rather than trying to bypass it. AI Help can explain these steps and read authorized records, but cannot perform changes for you.

An administrator invites people from User Management: choose Invite person, enter their email, select the workspace role they need and create the invitation. They join through the invitation link by signing in or creating their account. The assigned role determines access after they join. Administrators can manage people and roles; an ordinary member cannot invite people or change roles. Menus follow effective permissions, not merely the name of a role. A product can explicitly delegate a management capability through a custom role.

User questions may be asked in English or French; AI answers should follow the user's language. Use the application's language switcher for interface labels. If you need help with a feature not described by the supplied guides, the assistant must say it does not have that product's documentation instead of inventing business behavior. Ask a focused follow-up such as “Where do I start?” or “Explain the next step” to continue the current conversation.

## Modular monolith and layers

Trykatch is a .NET modular monolith: one composed application with explicitly registered business modules, not an automatically discovered collection of assemblies. A module has Domain, Application, IntegrationEvents, Presentation and Infrastructure projects. Domain owns invariants; Application owns use cases and authorization; Presentation binds HTTP; Infrastructure owns persistence and registration. IntegrationEvents is the allowed cross-module event contract. A web-capable module can also own a Web package.

The host references module Infrastructure entrypoints and registers only enabled modules through generated explicit registries. Presentation does not reference Infrastructure. Another module must not import another module's private implementation. Use an explicitly declared extension point or IntegrationEvents contract instead. Architecture tests enforce the project boundaries. Inspect the enabled module facts accompanying this guide rather than assuming every example is installed.

## Request lifecycle

An organization-scoped request authenticates the caller, resolves active membership and selected workspace, checks the relevant permission, and executes within the organization's transaction. PostgreSQL transaction-local app.organization_id and app.actor_id are set before scoped data access. Business use cases repeat permission checks through the installed authorizer. EF ownership filters and forced PostgreSQL row-level security are additional boundaries, not replacements for authentication or authorization.

The organization is the customer-facing workspace. Tenant describes the technical isolation mechanism. Platform administration and organization permissions are separate catalogs: being a platform operator is not permission to browse organization records through an assistant.

## Frontend and contracts

React uses generated TanStack Query clients from build-time OpenAPI. Module manifests declare routes, navigation and extension points; generated registries compose enabled modules. Application-owned overrides customize composition without editing generated files. Shared UI primitives, dialogs and DataTable own reusable interaction behavior. Use same-origin HttpOnly cookies and antiforgery for browser writes; do not store access or refresh tokens in React storage.

Rebuild the API before generating its client. API operation IDs, module IDs, permission keys, event contracts and named extension IDs are stable public contracts. Templates do not retroactively update an existing generated application.
