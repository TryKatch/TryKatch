# Scalable role-management interaction

Date: 2026-09-07

## Decision

Organization roles use a compact master/detail table. Summary rows show identity, type, lifecycle state, and permission/module counts. A dedicated chevron reveals one role at a time, and the revealed permission grants are grouped into independently collapsible catalog modules.

The shared `DataTable<T>` module owns disclosure state, paging, accessibility, and responsive behavior. User Management owns the role-specific mapping from stable permission keys to catalog names, descriptions, sensitivity, and modules. Search covers both human metadata and stable keys, and missing catalog definitions remain visible rather than failing open or disappearing.

This follows the progressive-disclosure and grouping patterns documented by [Microsoft Entra](https://learn.microsoft.com/en-us/entra/identity/role-based-access-control/permissions-reference), [Google Cloud IAM](https://docs.cloud.google.com/iam/docs/creating-custom-roles), and [Apple Business](https://support.apple.com/en-gb/guide/business/axmb46d473c7/web).
