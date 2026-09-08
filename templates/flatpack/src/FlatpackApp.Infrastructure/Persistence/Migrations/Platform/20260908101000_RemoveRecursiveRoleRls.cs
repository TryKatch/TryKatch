using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlatpackApp.Infrastructure.Persistence.Migrations.Platform;

[DbContext(typeof(PlatformDbContext))]
[Migration("20260908101000_RemoveRecursiveRoleRls")]
public sealed class RemoveRecursiveRoleRls : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP POLICY roles_read ON platform.roles;
            CREATE POLICY roles_read ON platform.roles FOR SELECT
              USING (
                COALESCE(NULLIF(current_setting('app.platform_admin', true), '')::boolean, false)
                OR "OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP POLICY roles_read ON platform.roles;
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
            """);
    }
}
