using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlatpackApp.Infrastructure.Persistence.Migrations.Application
{
    /// <inheritdoc />
    public partial class EnrichAuditActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Details",
                schema: "platform",
                table: "audit_entries",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "SubjectDisplayName",
                schema: "platform",
                table: "audit_entries",
                type: "character varying(240)",
                maxLength: 240,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("""
                ALTER TABLE platform.audit_entries NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE app.projects NO FORCE ROW LEVEL SECURITY;

                UPDATE platform.audit_entries AS audit
                SET "SubjectDisplayName" = project."Name"
                FROM app.projects AS project
                WHERE audit."SubjectType" = 'Project'
                  AND audit."SubjectId" = project."Id"::text
                  AND audit."SubjectDisplayName" = '';

                UPDATE platform.audit_entries AS audit
                SET "SubjectDisplayName" = role."Name"
                FROM platform.roles AS role
                WHERE audit."SubjectType" = 'Role'
                  AND audit."SubjectId" = role."Id"::text
                  AND audit."SubjectDisplayName" = '';

                UPDATE platform.audit_entries AS audit
                SET "SubjectDisplayName" = invitation."Email"
                FROM platform.invitations AS invitation
                WHERE audit."SubjectType" = 'Invitation'
                  AND audit."SubjectId" = invitation."Id"::text
                  AND audit."SubjectDisplayName" = '';

                UPDATE platform.audit_entries AS audit
                SET "SubjectDisplayName" = COALESCE(NULLIF(account."DisplayName", ''), account."Email", 'Member ' || LEFT(audit."SubjectId", 8))
                FROM platform.memberships AS membership
                JOIN identity."AspNetUsers" AS account ON account."Id" = membership."UserId"
                WHERE audit."SubjectType" = 'Membership'
                  AND audit."SubjectId" = membership."Id"::text
                  AND audit."SubjectDisplayName" = '';

                ALTER TABLE app.projects FORCE ROW LEVEL SECURITY;
                ALTER TABLE platform.audit_entries FORCE ROW LEVEL SECURITY;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Details",
                schema: "platform",
                table: "audit_entries");

            migrationBuilder.DropColumn(
                name: "SubjectDisplayName",
                schema: "platform",
                table: "audit_entries");
        }
    }
}
