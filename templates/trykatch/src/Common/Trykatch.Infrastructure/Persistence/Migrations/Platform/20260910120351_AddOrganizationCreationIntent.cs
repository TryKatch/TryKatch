using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trykatch.Infrastructure.Persistence.Migrations.Platform
{
    /// <inheritdoc />
    public partial class AddOrganizationCreationIntent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "organization_creation_intents",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    InitiatingActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    AdministratorEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    IdempotencyIdentityHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    Placement = table.Column<int>(type: "integer", nullable: false),
                    InvitationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProtectedInvitationToken = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_creation_intents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_organization_creation_intents_invitations_InvitationId",
                        column: x => x.InvitationId,
                        principalSchema: "platform",
                        principalTable: "invitations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_organization_creation_intents_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "platform",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_organization_creation_intents_InvitationId",
                schema: "platform",
                table: "organization_creation_intents",
                column: "InvitationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_organization_creation_intents_OrganizationId",
                schema: "platform",
                table: "organization_creation_intents",
                column: "OrganizationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_organization_creation_intents_Slug",
                schema: "platform",
                table: "organization_creation_intents",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "organization_creation_intents",
                schema: "platform");
        }
    }
}
