# Invitation roles and permission-driven navigation

## Outcome and scope

Administrators choose a person's organization role when creating an invitation. Ordinary members do not see administration menus or user-management controls. Role details use friendly names and descriptions, not permission identifiers. This improves the existing organization administration surface; platform access remains separate.

## Evidence and decisions

Requested during preview.27 UAT. The existing invitation entity already records a role and acceptance assigns it. Creation previously forced Member. Core sidebar links lacked permission metadata even though module navigation already used effective permissions. Management writes already require permission policies and fresh, transaction-locked authority.

“Admin-only” means effective administrative capabilities, not hard-coded role-name checks: protected Owner and seeded Admin retain their authority; an explicitly delegated custom role can expose only its assigned capabilities. Ordinary Member and Viewer default grants do not include people or role management. Existing read-only directory permissions are unchanged. No existing role assignments are silently rewritten.

## Ownership and business rules

The host's organization administration use case owns invitation creation. Optional `roleId` selects one active role in the authenticated organization; omitted values retain the existing Member default for API compatibility. Unknown, archived or foreign role IDs fail validation before storing an invitation. The server's existing delegation and protected Owner checks also apply before generating a token or writing an invitation. The role cannot be changed on a pending invitation; revoke and reissue instead. Acceptance rechecks role availability under the existing lock.

## Permissions

- Invitation creation and member changes require `members.manage` in HTTP and the application use case, with fresh authority checked under the organization lock.
- Role changes require `roles.manage` and the existing delegation checks.
- User Management navigation requires at least one of those management permissions. Read-only directory permissions alone do not expose the administration page.
- Audit requires `audit.read`. Recovery requires an applicable management permission, including enabled modules' contributed recovery permissions.
- Sidebar and command palette share the same permission-filtered navigation. Unknown/new module permissions are not special-cased by role name. Direct URLs do not grant API access.

## Implementation and compatibility

No schema migration is required. Invitation DTOs include the recorded role ID. React submits the selected role ID, shows its friendly name in pending invitations and confirmation, and excludes roles whose `canAssign` hint is false. That hint is not an authorization grant. Generated OpenAPI and clients must be regenerated, not hand-edited. English and French interface strings are supplied. Technical keys remain in the backend contract and developer facts, not the role disclosure.

When optional SMTP is enabled, invitation emails include the selected role's friendly name in both plain text and HTML. The application use case obtains that name from the validated organization role, never from caller-supplied presentation text. Custom role names are HTML-encoded. The role name is notification information, not a new access grant; acceptance still checks the saved role ID. Custom `IInvitationNotifier` implementations must accept the `roleName` argument between organization name and invitation URL. Without SMTP, the copyable invitation link and recorded role remain available.

Invitation and password-reset notifications share a branded HTML shell with an application header, blue primary action, neutral surfaces, plain-text alternatives and a fallback link. Branding belongs to operator configuration (`Email:Branding`), not untrusted request input or personal themes; colours accept only six-digit hex and external logos must use HTTPS without credentials. Test delivery locally with Mailpit, not real recipients. A production SMTP provider remains an explicit deployment choice; local rendering does not prove production inbox delivery or every email client's rendering.

## Acceptance cases

Review correction: managers with `members.read` and `members.manage` but without `roles.read` can submit an invitation without a role ID, retaining the existing server-selected Member default. The form explains this fallback and never queries the restricted role or permission catalog. Managers with role-read access still select an assignable role; an empty/unassignable catalog does not trigger fallback. Server delegation checks can reject the default role with 403, and the UI preserves the recipient for correction. Unit regression and isolated Storybook success/denial interactions cover this boundary; mocks are not server-security evidence.

1. Select Viewer or Admin, accept through production HTTP and runtime PostgreSQL roles, and receive exactly the selected role's grants.
2. A Member's attempt to invite, edit the Owner or create a privileged role receives 403 and creates no invitation.
3. Unknown/foreign role IDs and an equivalent-permission non-Owner attempting an Owner invitation are denied without storing an invitation.
4. Ordinary members and loading access states do not expose administrative links; new module links respond to effective permission changes.
5. The invitation form submits the selected role, excludes unassignable roles and displays the assigned friendly role name.
6. A Member opening the management URL sees no administrative controls and triggers no administration data queries.
7. Role details contain friendly names and descriptions, not dotted permission keys. Test changed flows at 320, 768 and 1440 pixels in light/dark themes.
8. Selected Admin/Viewer/Member invitations pass the validated role name to notification delivery; plain-text and HTML emails contain it, and custom names cannot inject HTML. Existing SMTP transport-security checks remain enforced.

## Verification record

Component tests verify cases 4–6; the permission-label regression went red before removing the technical keys and now passes. Four production HTTP/PostgreSQL cases verify selected Admin/Viewer acceptance, member-management 403s and unknown-role/protected-Owner denials without skips. The expanded denial case also passes explicit foreign/archived role fixtures after correcting its required role-creation ID field. Browser checks cover invitation radios and expanded friendly role details at 320/768/1440px in light/dark themes; screenshots were inspected. The radio regression found a competing generic form-label selector and the corrected selector now wins. The full final-state integration rerun is tracked in the delivery audit.
