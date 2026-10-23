namespace Trykatch.Infrastructure.Organizations;

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
    private const string Actor = "(\"ActorId\" = (NULLIF(current_setting('app.actor_id'::text, true), ''::text))::uuid)";
    private const string OutboxWorker = "(CURRENT_USER = 'trykatch_outbox_worker'::name)";
    private const string PlatformRuntime = "(CURRENT_USER = 'trykatch_platform_runtime'::name)";
    private const string Coordinator = "(CURRENT_USER = 'trykatch_work_coordinator'::name)";
    private const string RequestedTime = "(\"RequestedAt\" = CURRENT_TIMESTAMP)";
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

    public static IReadOnlyDictionary<string, Policy[]> All { get; } = Create();

    private static Dictionary<string, Policy[]> Create()
    {
        Dictionary<string, Policy[]> policies = new(StringComparer.Ordinal)
    {
        ["infrastructure.module_work_items"] =
        [
            new("module_work_items_append", "a", "", $"({Organization} AND {Actor})", ["trykatch_org_runtime"]),
            new("module_work_items_coordinate", "*", Coordinator, Coordinator, ["trykatch_work_coordinator"])
        ],
        ["infrastructure.module_event_deliveries"] =
        [
            new("module_event_deliveries_outbox_read", "r", OutboxWorker, Roles: ["trykatch_outbox_worker"]),
            new("module_event_deliveries_outbox_insert", "a", "", OutboxWorker, ["trykatch_outbox_worker"]),
            new("module_event_deliveries_coordinate", "*", Coordinator, Coordinator, ["trykatch_work_coordinator"])
        ],
        ["app.module_work_completions"] =
        [
            new("module_work_completions_read", "r", Organization, Roles: ["trykatch_org_runtime"]),
            new("module_work_completions_insert", "a", "", $"({Organization} AND {Actor})", ["trykatch_org_runtime"])
        ],
        ["app.module_event_inbox"] =
        [
            new("module_event_inbox_read", "r", Organization, Roles: ["trykatch_org_runtime"]),
            new("module_event_inbox_insert", "a", "", $"({Organization} AND {Actor})", ["trykatch_org_runtime"])
        ],
        ["platform.organization_assistant_settings"] = [new("organization_assistant_settings_isolation", "*", Organization, Organization)],
        ["platform.audit_entries"] =
        [
            new("audit_organization_isolation", "*", Organization, $"({Organization} AND {Actor})"),
            new("audit_projection_worker_read", "r", OutboxWorker, Roles: ["trykatch_outbox_worker"])
        ],
        ["platform.audit_intents"] =
        [
            new("audit_intents_append", "a", "", $"({Organization} AND {Actor})", ["trykatch_org_runtime"]),
            new("audit_intents_worker_read", "r", OutboxWorker, Roles: ["trykatch_outbox_worker"])
        ],
        ["platform.outbox_replay_requests"] =
        [
            new("outbox_replay_requests_platform_read", "r", PlatformRuntime, Roles: ["trykatch_platform_runtime"]),
            new("outbox_replay_requests_platform_insert", "a", "", $"({PlatformRuntime} AND {Actor} AND {RequestedTime})", ["trykatch_platform_runtime"]),
            new("outbox_replay_requests_worker_read", "r", OutboxWorker, Roles: ["trykatch_outbox_worker"])
        ],
        ["platform.outbox_recovery_events"] =
        [
            new("outbox_recovery_events_platform_read", "r", PlatformRuntime, Roles: ["trykatch_platform_runtime"]),
            new("outbox_recovery_events_worker_read", "r", OutboxWorker, Roles: ["trykatch_outbox_worker"]),
            new("outbox_recovery_events_worker_insert", "a", "", OutboxWorker, ["trykatch_outbox_worker"])
        ],
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
        foreach (string table in Jobs.QuartzSchemaContract.Tables.Concat(["module_schedule_cursors", "module_work_controls"]))
            policies.Add("infrastructure." + table, [new(table + "_coordinate", "*", Coordinator, Coordinator, ["trykatch_work_coordinator"])]);
        return policies;
    }
}
