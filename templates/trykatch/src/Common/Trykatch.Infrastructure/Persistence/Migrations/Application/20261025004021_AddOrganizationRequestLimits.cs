using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trykatch.Infrastructure.Persistence.Migrations.Application
{
    /// <inheritdoc />
    public partial class AddOrganizationRequestLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "organization_request_limits",
                schema: "app",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Policy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    WindowStartUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_request_limits", x => new { x.OrganizationId, x.Policy });
                    table.CheckConstraint("CK_organization_request_limits_identity", "\"OrganizationId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"Policy\" = 'organization-api'");
                    table.CheckConstraint("CK_organization_request_limits_window", "isfinite(\"WindowStartUtc\") AND \"Attempts\" BETWEEN 1 AND 1000000");
                });
            migrationBuilder.Sql("""
                ALTER TABLE app.organization_request_limits ENABLE ROW LEVEL SECURITY;
                ALTER TABLE app.organization_request_limits FORCE ROW LEVEL SECURITY;
                CREATE POLICY organization_request_limits_read ON app.organization_request_limits
                    FOR SELECT TO trykatch_org_runtime
                    USING ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid
                        AND NULLIF(current_setting('app.actor_id', true), '')::uuid <> '00000000-0000-0000-0000-000000000000'::uuid);
                CREATE POLICY organization_request_limits_insert ON app.organization_request_limits
                    FOR INSERT TO trykatch_org_runtime
                    WITH CHECK ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid
                        AND NULLIF(current_setting('app.actor_id', true), '')::uuid <> '00000000-0000-0000-0000-000000000000'::uuid);
                CREATE POLICY organization_request_limits_update ON app.organization_request_limits
                    FOR UPDATE TO trykatch_org_runtime
                    USING ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid
                        AND NULLIF(current_setting('app.actor_id', true), '')::uuid <> '00000000-0000-0000-0000-000000000000'::uuid)
                    WITH CHECK ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid
                        AND NULLIF(current_setting('app.actor_id', true), '')::uuid <> '00000000-0000-0000-0000-000000000000'::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "organization_request_limits",
                schema: "app");
        }
    }
}
