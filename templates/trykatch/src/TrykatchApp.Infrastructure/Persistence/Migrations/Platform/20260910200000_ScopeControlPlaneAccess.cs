using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TrykatchApp.Infrastructure.Persistence.Migrations.Platform;

[DbContext(typeof(PlatformDbContext))]
[Migration("20260910200000_ScopeControlPlaneAccess")]
public sealed class ScopeControlPlaneAccess : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE platform.memberships ENABLE ROW LEVEL SECURITY;
            ALTER TABLE platform.memberships FORCE ROW LEVEL SECURITY;
            ALTER TABLE platform.roles ENABLE ROW LEVEL SECURITY;
            ALTER TABLE platform.roles FORCE ROW LEVEL SECURITY;
            ALTER TABLE platform.membership_roles ENABLE ROW LEVEL SECURITY;
            ALTER TABLE platform.membership_roles FORCE ROW LEVEL SECURITY;
            ALTER TABLE platform.role_permissions ENABLE ROW LEVEL SECURITY;
            ALTER TABLE platform.role_permissions FORCE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS memberships_read ON platform.memberships;
            DROP POLICY IF EXISTS memberships_write ON platform.memberships;
            CREATE POLICY memberships_read ON platform.memberships FOR SELECT
              USING ("UserId" = NULLIF(current_setting('app.actor_id', true), '')::uuid);
            CREATE POLICY memberships_write ON platform.memberships FOR ALL
              USING ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid)
              WITH CHECK ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid);

            DROP POLICY IF EXISTS roles_read ON platform.roles;
            DROP POLICY IF EXISTS roles_write ON platform.roles;
            CREATE POLICY roles_write ON platform.roles FOR ALL
              USING ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid)
              WITH CHECK ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid);

            DROP POLICY IF EXISTS membership_roles_read ON platform.membership_roles;
            DROP POLICY IF EXISTS membership_roles_write ON platform.membership_roles;
            CREATE POLICY membership_roles_write ON platform.membership_roles FOR ALL
              USING (EXISTS (SELECT 1 FROM platform.memberships m JOIN platform.roles r ON r."OrganizationId" = m."OrganizationId"
                WHERE m."Id" = membership_roles."MembershipId" AND r."Id" = membership_roles."RoleId"
                  AND m."OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid))
              WITH CHECK (EXISTS (SELECT 1 FROM platform.memberships m JOIN platform.roles r ON r."OrganizationId" = m."OrganizationId"
                WHERE m."Id" = membership_roles."MembershipId" AND r."Id" = membership_roles."RoleId"
                  AND m."OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid));

            DROP POLICY IF EXISTS role_permissions_read ON platform.role_permissions;
            DROP POLICY IF EXISTS role_permissions_write ON platform.role_permissions;
            CREATE POLICY role_permissions_write ON platform.role_permissions FOR ALL
              USING (EXISTS (SELECT 1 FROM platform.roles r WHERE r."Id" = role_permissions."RoleId"
                AND r."OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid))
              WITH CHECK (EXISTS (SELECT 1 FROM platform.roles r WHERE r."Id" = role_permissions."RoleId"
                AND r."OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid));

            ALTER TABLE platform.invitations ENABLE ROW LEVEL SECURITY;
            ALTER TABLE platform.invitations FORCE ROW LEVEL SECURITY;
            CREATE POLICY invitations_token_read ON platform.invitations FOR SELECT
              USING ("TokenHash" = NULLIF(current_setting('app.invitation_hash', true), ''));
            CREATE POLICY invitations_organization ON platform.invitations FOR ALL
              USING ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid)
              WITH CHECK ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid
                AND EXISTS (SELECT 1 FROM platform.roles r WHERE r."Id" = invitations."RoleId"
                  AND r."OrganizationId" = invitations."OrganizationId"));

            ALTER TABLE platform.organizations ENABLE ROW LEVEL SECURITY;
            ALTER TABLE platform.organizations FORCE ROW LEVEL SECURITY;
            CREATE POLICY organizations_platform ON platform.organizations FOR ALL
              USING (CURRENT_USER = 'trykatch_platform_runtime')
              WITH CHECK (CURRENT_USER = 'trykatch_platform_runtime');
            CREATE POLICY organizations_actor_read ON platform.organizations FOR SELECT
              USING ("Id" = NULLIF(current_setting('app.organization_id', true), '')::uuid
                OR EXISTS (SELECT 1 FROM platform.memberships m WHERE m."OrganizationId" = organizations."Id"
                  AND m."UserId" = NULLIF(current_setting('app.actor_id', true), '')::uuid));
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Tenant-scoped control-plane enforcement must not be removed.");
}
