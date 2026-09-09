using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrykatchApp.Infrastructure.Persistence.Migrations.Platform;

[DbContext(typeof(PlatformDbContext))]
[Migration("20260908090000_EnforceAccessControlRls")]
public sealed class EnforceAccessControlRls : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE platform.memberships ENABLE ROW LEVEL SECURITY;
            ALTER TABLE platform.memberships FORCE ROW LEVEL SECURITY;
            CREATE POLICY memberships_read ON platform.memberships FOR SELECT
              USING (
                COALESCE(NULLIF(current_setting('app.platform_admin', true), '')::boolean, false)
                OR "OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid
                OR "UserId" = NULLIF(current_setting('app.actor_id', true), '')::uuid);
            CREATE POLICY memberships_write ON platform.memberships FOR ALL
              USING (
                COALESCE(NULLIF(current_setting('app.platform_admin', true), '')::boolean, false)
                OR "OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid)
              WITH CHECK (
                COALESCE(NULLIF(current_setting('app.platform_admin', true), '')::boolean, false)
                OR "OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid);

            ALTER TABLE platform.roles ENABLE ROW LEVEL SECURITY;
            ALTER TABLE platform.roles FORCE ROW LEVEL SECURITY;
            CREATE POLICY roles_read ON platform.roles FOR SELECT
              USING (
                COALESCE(NULLIF(current_setting('app.platform_admin', true), '')::boolean, false)
                OR "OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid
                OR EXISTS (
                  SELECT 1
                  FROM platform.membership_roles mr
                  JOIN platform.memberships m ON m."Id" = mr."MembershipId"
                  WHERE mr."RoleId" = roles."Id"
                    AND m."UserId" = NULLIF(current_setting('app.actor_id', true), '')::uuid));
            CREATE POLICY roles_write ON platform.roles FOR ALL
              USING (
                COALESCE(NULLIF(current_setting('app.platform_admin', true), '')::boolean, false)
                OR "OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid)
              WITH CHECK (
                COALESCE(NULLIF(current_setting('app.platform_admin', true), '')::boolean, false)
                OR "OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid);

            ALTER TABLE platform.membership_roles ENABLE ROW LEVEL SECURITY;
            ALTER TABLE platform.membership_roles FORCE ROW LEVEL SECURITY;
            CREATE POLICY membership_roles_read ON platform.membership_roles FOR SELECT
              USING (
                COALESCE(NULLIF(current_setting('app.platform_admin', true), '')::boolean, false)
                OR EXISTS (
                  SELECT 1 FROM platform.memberships m
                  WHERE m."Id" = membership_roles."MembershipId"
                    AND (m."OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid
                      OR m."UserId" = NULLIF(current_setting('app.actor_id', true), '')::uuid)));
            CREATE POLICY membership_roles_write ON platform.membership_roles FOR ALL
              USING (
                COALESCE(NULLIF(current_setting('app.platform_admin', true), '')::boolean, false)
                OR EXISTS (
                  SELECT 1 FROM platform.memberships m
                  WHERE m."Id" = membership_roles."MembershipId"
                    AND m."OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid))
              WITH CHECK (
                COALESCE(NULLIF(current_setting('app.platform_admin', true), '')::boolean, false)
                OR EXISTS (
                  SELECT 1 FROM platform.memberships m
                  JOIN platform.roles r ON r."OrganizationId" = m."OrganizationId"
                  WHERE m."Id" = membership_roles."MembershipId"
                    AND r."Id" = membership_roles."RoleId"
                    AND m."OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid));

            ALTER TABLE platform.role_permissions ENABLE ROW LEVEL SECURITY;
            ALTER TABLE platform.role_permissions FORCE ROW LEVEL SECURITY;
            CREATE POLICY role_permissions_read ON platform.role_permissions FOR SELECT
              USING (
                COALESCE(NULLIF(current_setting('app.platform_admin', true), '')::boolean, false)
                OR EXISTS (
                  SELECT 1 FROM platform.roles r
                  WHERE r."Id" = role_permissions."RoleId"
                    AND (r."OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid
                      OR EXISTS (
                        SELECT 1 FROM platform.membership_roles mr
                        JOIN platform.memberships m ON m."Id" = mr."MembershipId"
                        WHERE mr."RoleId" = r."Id"
                          AND m."UserId" = NULLIF(current_setting('app.actor_id', true), '')::uuid))));
            CREATE POLICY role_permissions_write ON platform.role_permissions FOR ALL
              USING (
                COALESCE(NULLIF(current_setting('app.platform_admin', true), '')::boolean, false)
                OR EXISTS (
                  SELECT 1 FROM platform.roles r
                  WHERE r."Id" = role_permissions."RoleId"
                    AND r."OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid))
              WITH CHECK (
                COALESCE(NULLIF(current_setting('app.platform_admin', true), '')::boolean, false)
                OR EXISTS (
                  SELECT 1 FROM platform.roles r
                  WHERE r."Id" = role_permissions."RoleId"
                    AND r."OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid));
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP POLICY IF EXISTS role_permissions_write ON platform.role_permissions;
            DROP POLICY IF EXISTS role_permissions_read ON platform.role_permissions;
            ALTER TABLE platform.role_permissions NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE platform.role_permissions DISABLE ROW LEVEL SECURITY;

            DROP POLICY IF EXISTS membership_roles_write ON platform.membership_roles;
            DROP POLICY IF EXISTS membership_roles_read ON platform.membership_roles;
            ALTER TABLE platform.membership_roles NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE platform.membership_roles DISABLE ROW LEVEL SECURITY;

            DROP POLICY IF EXISTS roles_write ON platform.roles;
            DROP POLICY IF EXISTS roles_read ON platform.roles;
            ALTER TABLE platform.roles NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE platform.roles DISABLE ROW LEVEL SECURITY;

            DROP POLICY IF EXISTS memberships_write ON platform.memberships;
            DROP POLICY IF EXISTS memberships_read ON platform.memberships;
            ALTER TABLE platform.memberships NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE platform.memberships DISABLE ROW LEVEL SECURITY;
            """);
    }
}
