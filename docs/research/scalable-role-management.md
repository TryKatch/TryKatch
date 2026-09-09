# Scalable role-management interaction

Date: 2026-09-07

## Question

How should Trykatch keep organization roles understandable when an installation has many roles and each role contains many permissions?

## Primary-source findings

- Microsoft Entra publishes a compact master table of roles with a human-readable description, then presents the action list as detail for the selected role. The catalog currently contains well over one hundred built-in roles, so the master list does not attempt to render every permission inline. [Microsoft Entra built-in roles](https://learn.microsoft.com/en-us/entra/identity/role-based-access-control/permissions-reference)
- Google Cloud separates role metadata from `includedPermissions`, makes permission metadata searchable, and filters the custom-role permission picker by service and permission type. A custom role can contain up to 3,000 permissions, which makes progressive disclosure and filtering requirements rather than cosmetic choices. [Create and manage custom roles](https://docs.cloud.google.com/iam/docs/creating-custom-roles), [IAM roles and permissions index](https://cloud.google.com/iam/docs/permissions-reference)
- Apple Business presents a role first, then lets the administrator inspect or edit privileges grouped into domains such as Organization, People, Devices, Apps & Services, and Brands. [View and assign roles in Apple Business](https://support.apple.com/en-gb/guide/business/axmb46d473c7/web)
- GitHub defines a role as a set of permissions, separates predefined roles from custom roles, and recommends choosing the role that matches a person's function without granting more access than needed. [Roles in an organization](https://docs.github.com/en/organizations/managing-peoples-access-to-your-organization-with-roles/roles-in-an-organization)

## Trykatch decision

Use a compact master/detail table rather than permission chips in every row:

1. Each row shows only the role name, ownership type, lifecycle state, grant count, module count, and actions.
2. A dedicated chevron reveals one role at a time. Opening another role closes the previous detail, keeping the collection bounded.
3. The detail groups grants by the backend permission catalog's module metadata. Module sections are independently collapsible and initially closed.
4. Stored keys missing from the current catalog remain visible in an explicit unavailable-definitions group so permission drift cannot disappear silently.
5. Search covers role names, stable keys, human permission names, and descriptions. A role-type filter separates system and custom roles.
6. The shared table pages bounded client-side role results in groups of 20. If role collections become unbounded, the same table interface should receive a server-paged adapter; the disclosure behavior does not change.

This is a deep-module split: `DataTable<T>` owns expansion, one-at-a-time state, paging, keyboard semantics, and responsive table behavior. The user-management feature owns the meaning and grouping of permission data. Other collection pages can reuse the table interface without learning role-specific rules.

## Rejected alternatives

- **All permissions as chips in each row:** scannability and row height collapse as modules grow.
- **A separate card per role:** cards waste horizontal space and make sorting, filtering, column choice, and comparison harder.
- **A full editor inside an expanded row:** disclosure is for inspection. Editing remains a deliberate dialog with grant-boundary warnings and validation.
- **Only a separate details page:** useful at very large scale, but unnecessarily interrupts comparison for the current organization-sized catalog. The row disclosure can later link to a dedicated route without replacing the table.
