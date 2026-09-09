using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrykatchApp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInvitationRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RoleId",
                schema: "platform",
                table: "invitations",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE platform.invitations AS invitation
                SET "RoleId" = role."Id"
                FROM platform.roles AS role
                WHERE role."OrganizationId" = invitation."OrganizationId"
                  AND role."Name" = 'Member'
                  AND role."IsSystem" = TRUE;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "RoleId",
                schema: "platform",
                table: "invitations",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_invitations_RoleId",
                schema: "platform",
                table: "invitations",
                column: "RoleId");

            migrationBuilder.AddForeignKey(
                name: "FK_invitations_roles_RoleId",
                schema: "platform",
                table: "invitations",
                column: "RoleId",
                principalSchema: "platform",
                principalTable: "roles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_invitations_roles_RoleId",
                schema: "platform",
                table: "invitations");

            migrationBuilder.DropIndex(
                name: "IX_invitations_RoleId",
                schema: "platform",
                table: "invitations");

            migrationBuilder.DropColumn(
                name: "RoleId",
                schema: "platform",
                table: "invitations");
        }
    }
}
