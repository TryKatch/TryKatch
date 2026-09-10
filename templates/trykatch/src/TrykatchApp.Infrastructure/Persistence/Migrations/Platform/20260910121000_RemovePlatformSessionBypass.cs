using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrykatchApp.Infrastructure.Persistence.Migrations.Platform;

[DbContext(typeof(PlatformDbContext))]
[Migration("20260910121000_RemovePlatformSessionBypass")]
public sealed class RemovePlatformSessionBypass : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP POLICY IF EXISTS memberships_read ON platform.memberships;
            DROP POLICY IF EXISTS memberships_write ON platform.memberships;
            CREATE POLICY memberships_read ON platform.memberships FOR SELECT
              USING (
                current_user = 'trykatch_platform_runtime'
                OR "OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid
                OR "UserId" = NULLIF(current_setting('app.actor_id', true), '')::uuid);
            CREATE POLICY memberships_write ON platform.memberships FOR ALL
              USING (
                current_user = 'trykatch_platform_runtime'
                OR "OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid)
              WITH CHECK (
                current_user = 'trykatch_platform_runtime'
                OR "OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid);

            DROP POLICY IF EXISTS roles_read ON platform.roles;
            DROP POLICY IF EXISTS roles_write ON platform.roles;
            CREATE POLICY roles_read ON platform.roles FOR SELECT
              USING (
                current_user = 'trykatch_platform_runtime'
                OR "OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid);
            CREATE POLICY roles_write ON platform.roles FOR ALL
              USING (
                current_user = 'trykatch_platform_runtime'
                OR "OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid)
              WITH CHECK (
                current_user = 'trykatch_platform_runtime'
                OR "OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid);

            DROP POLICY IF EXISTS membership_roles_read ON platform.membership_roles;
            DROP POLICY IF EXISTS membership_roles_write ON platform.membership_roles;
            CREATE POLICY membership_roles_read ON platform.membership_roles FOR SELECT
              USING (
                current_user = 'trykatch_platform_runtime'
                OR EXISTS (
                  SELECT 1 FROM platform.memberships m
                  WHERE m."Id" = membership_roles."MembershipId"
                    AND m."OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid));
            CREATE POLICY membership_roles_write ON platform.membership_roles FOR ALL
              USING (
                current_user = 'trykatch_platform_runtime'
                OR EXISTS (
                  SELECT 1 FROM platform.memberships m
                  WHERE m."Id" = membership_roles."MembershipId"
                    AND m."OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid))
              WITH CHECK (
                current_user = 'trykatch_platform_runtime'
                OR EXISTS (
                  SELECT 1 FROM platform.memberships m
                  JOIN platform.roles r ON r."OrganizationId" = m."OrganizationId"
                  WHERE m."Id" = membership_roles."MembershipId"
                    AND r."Id" = membership_roles."RoleId"
                    AND m."OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid));

            DROP POLICY IF EXISTS role_permissions_read ON platform.role_permissions;
            DROP POLICY IF EXISTS role_permissions_write ON platform.role_permissions;
            CREATE POLICY role_permissions_read ON platform.role_permissions FOR SELECT
              USING (
                current_user = 'trykatch_platform_runtime'
                OR EXISTS (
                  SELECT 1 FROM platform.roles r
                  WHERE r."Id" = role_permissions."RoleId"
                    AND r."OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid));
            CREATE POLICY role_permissions_write ON platform.role_permissions FOR ALL
              USING (
                current_user = 'trykatch_platform_runtime'
                OR EXISTS (
                  SELECT 1 FROM platform.roles r
                  WHERE r."Id" = role_permissions."RoleId"
                    AND r."OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid))
              WITH CHECK (
                current_user = 'trykatch_platform_runtime'
                OR EXISTS (
                  SELECT 1 FROM platform.roles r
                  WHERE r."Id" = role_permissions."RoleId"
                    AND r."OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid));
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("The removed session-variable privilege bypass must not be restored.");
}
