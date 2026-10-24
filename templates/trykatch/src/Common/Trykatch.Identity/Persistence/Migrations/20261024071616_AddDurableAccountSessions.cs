using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trykatch.Identity.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableAccountSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "account_sessions",
                schema: "identity",
                columns: table => new
                {
                    SessionHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SecurityStamp = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkspaceGeneration = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_sessions", x => x.SessionHash);
                    table.CheckConstraint("CK_account_sessions_expiry", "\"ExpiresAt\" > \"CreatedAt\"");
                    table.CheckConstraint("CK_account_sessions_generation", "\"WorkspaceGeneration\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_account_sessions_hash", "\"SessionHash\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_account_sessions_organization", "\"OrganizationId\" IS NULL OR \"OrganizationId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_account_sessions_stamp", "length(\"SecurityStamp\") > 0");
                    table.ForeignKey(
                        name: "FK_account_sessions_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_account_sessions_UserId_ExpiresAt",
                schema: "identity",
                table: "account_sessions",
                columns: new[] { "UserId", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_sessions",
                schema: "identity");
        }
    }
}
