using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trykatch.Infrastructure.Persistence.Migrations.Platform
{
    /// <inheritdoc />
    public partial class AddRoleDescriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Description",
                schema: "platform",
                table: "roles",
                type: "character varying(240)",
                maxLength: 240,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("""
                UPDATE platform.roles
                SET "Description" = CASE "Name"
                    WHEN 'Owner' THEN 'Full control of this workspace, including access management.'
                    WHEN 'Admin' THEN 'Manage workspace operations, people, roles, and projects.'
                    WHEN 'Member' THEN 'Create and manage workspace projects.'
                    WHEN 'Viewer' THEN 'Read-only access to workspace projects.'
                    ELSE "Description"
                END
                WHERE "IsSystem" = TRUE AND "Description" = '';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Description",
                schema: "platform",
                table: "roles");
        }
    }
}
