namespace TrykatchApp.Infrastructure.Organizations;

/// <summary>
/// Exact PostgreSQL catalog expressions for the host-owned exceptions. Unlike
/// module tables, actor discovery and invitation lookup intentionally have
/// multiple narrowly scoped commands. Parentheses are significant here.
/// </summary>
internal static class HostPostgresPolicyContracts
{
    internal sealed record Policy(
        string Name,
        string Command,
        string Using,
        string WithCheck = "",
        string[]? Roles = null)
    {
        public IReadOnlyList<string> ExpectedRoles => Roles ?? ["PUBLIC"];
    }

    private const string Organization = "(\"OrganizationId\" = (NULLIF(current_setting('app.organization_id'::text, true), ''::text))::uuid)";
    private const string MembershipRole = """
        (EXISTS ( SELECT 1
        FROM (platform.memberships m JOIN platform.roles r ON ((r."OrganizationId" = m."OrganizationId")))
        WHERE ((m."Id" = membership_roles."MembershipId") AND (r."Id" = membership_roles."RoleId")
        AND (m."OrganizationId" = (NULLIF(current_setting('app.organization_id'::text, true), ''::text))::uuid))))
        """;
    private const string RolePermission = """
        (EXISTS ( SELECT 1 FROM platform.roles r
        WHERE ((r."Id" = role_permissions."RoleId")
        AND (r."OrganizationId" = (NULLIF(current_setting('app.organization_id'::text, true), ''::text))::uuid))))
        """;

    public static IReadOnlyDictionary<string, Policy[]> All { get; } = new Dictionary<string, Policy[]>(StringComparer.Ordinal)
    {
        ["platform.audit_entries"] = [new("audit_organization_isolation", "*", Organization,
            $"({Organization} AND (\"ActorId\" = (NULLIF(current_setting('app.actor_id'::text, true), ''::text))::uuid))")],
        ["platform.memberships"] =
        [
            new("memberships_read", "r", "(\"UserId\" = (NULLIF(current_setting('app.actor_id'::text, true), ''::text))::uuid)"),
            new("memberships_write", "*", Organization, Organization)
        ],
        ["platform.roles"] = [new("roles_write", "*", Organization, Organization)],
        ["platform.membership_roles"] = [new("membership_roles_write", "*", MembershipRole, MembershipRole)],
        ["platform.role_permissions"] = [new("role_permissions_write", "*", RolePermission, RolePermission)],
        ["platform.invitations"] =
        [
            new("invitations_token_read", "r", "((\"TokenHash\")::text = NULLIF(current_setting('app.invitation_hash'::text, true), ''::text))"),
            new("invitations_organization", "*", Organization, $"""
                ({Organization} AND (EXISTS ( SELECT 1 FROM platform.roles r
                WHERE ((r."Id" = invitations."RoleId") AND (r."OrganizationId" = invitations."OrganizationId")))))
                """)
        ],
        ["platform.organizations"] =
        [
            new("organizations_platform", "*", "(CURRENT_USER = 'trykatch_platform_runtime'::name)",
                "(CURRENT_USER = 'trykatch_platform_runtime'::name)", ["trykatch_platform_runtime"]),
            new("organizations_actor_read", "r", """
                (("Id" = (NULLIF(current_setting('app.organization_id'::text, true), ''::text))::uuid)
                OR (EXISTS ( SELECT 1 FROM platform.memberships m
                WHERE ((m."OrganizationId" = organizations."Id")
                AND (m."UserId" = (NULLIF(current_setting('app.actor_id'::text, true), ''::text))::uuid)))))
                """, Roles: ["trykatch_org_runtime"])
        ]
    };
}
