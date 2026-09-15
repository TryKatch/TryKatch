# __MODULE__ business module

The reviewed authoring contract is `module.blueprint.json`. This module owns `app.__RESOURCE__` and `/api/v1/__RESOURCE__`. Its Domain owns validation and transitions; Application contains explicit query/command handlers; Infrastructure supplies persistence and composition; Presentation supplies secured HTTP endpoints. IntegrationEvents are the cross-module contract.

## Run from the application root

Start Docker, then run `trykatch start`. Use the API and web URLs printed by Aspire. If this module was generated with `--with-web`, open `/__RESOURCE__` in the application. Backend-only generation does not add a React route.

```bash
dotnet test tests/Modules/__MODULE__/__ROOT_NAMESPACE__.Modules.__MODULE__.UnitTests
dotnet test tests/Modules/__MODULE__/__ROOT_NAMESPACE__.Modules.__MODULE__.ArchitectureTests
trykatch module doctor
```

## Security and workflow contract

- Organization identity comes from the authenticated workspace, never from editable request fields. Central EF filtering and forced PostgreSQL RLS enforce isolation. The Migrator applies the registered module migration before the API starts.
- `__MODULE_ID__.read` and `__MODULE_ID__.manage` control CRUD. Actions additionally require their declared submit/review permission. Browser mutations require antiforgery.
- Responses contain `version`, `workflowState`, `canEdit` and permission-filtered `availableActions`. Send `expectedVersion` on updates, actions and recovery operations. A stale version returns HTTP 409; refresh and deliberately retry.
- Action paths are `/api/v1/__RESOURCE__/{id}/actions/<kebab-case-action>`. Inspect the blueprint for declared transitions, inputs, rules and bilingual messages. Invalid rules return HTTP 400, forbidden actions 403 and invalid transitions 409.
- Audit and outbox writes participate in the request transaction. Archiving and recovery preserve workflow state; they do not reopen completed business decisions.

## Extend safely

Generated code belongs to this application. Add domain invariants and focused command handlers with tests, then create an explicit database migration and regenerate the OpenAPI client when contracts change. Editing the blueprint does not regenerate customized source; v1 refuses to overwrite an existing module. Keep cross-module dependencies limited to published integration events.
