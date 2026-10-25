using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trykatch.Infrastructure.Persistence.Migrations.Platform
{
    /// <inheritdoc />
    public partial class AddEntitlementPublicationHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "entitlement_publication_history",
                schema: "platform",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceRevision = table.Column<long>(type: "bigint", nullable: false),
                    SourceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Source = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    SourceVersion = table.Column<int>(type: "integer", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    InitialResourceCount = table.Column<int>(type: "integer", nullable: false),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entitlement_publication_history", x => new { x.OrganizationId, x.SourceRevision });
                    table.CheckConstraint("CK_entitlement_publication_history_identity", "\"OrganizationId\" <> '00000000-0000-0000-0000-000000000000'::uuid\nAND \"SourceRevision\" >= 0 AND \"ActorId\" <> '00000000-0000-0000-0000-000000000000'::uuid\nAND \"InitialResourceCount\" BETWEEN 0 AND 10000 AND isfinite(\"PublishedAtUtc\")");
                    table.CheckConstraint("CK_entitlement_publication_history_source", "\"SourceHash\" ~ '^[A-Fa-f0-9]{64}$'\nAND \"Source\" = 'host-configuration' AND \"SourceVersion\" = 1");
                    table.ForeignKey(
                        name: "FK_entitlement_publication_history_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "platform",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql("""
                ALTER TABLE platform.entitlement_publication_history ENABLE ROW LEVEL SECURITY;
                ALTER TABLE platform.entitlement_publication_history FORCE ROW LEVEL SECURITY;
                CREATE POLICY entitlement_publication_history_platform_read
                  ON platform.entitlement_publication_history FOR SELECT TO trykatch_platform_runtime
                  USING ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    AND NULLIF(current_setting('app.actor_id', true), '')::uuid <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND current_user = 'trykatch_platform_runtime');
                CREATE POLICY entitlement_publication_history_platform_insert
                  ON platform.entitlement_publication_history FOR INSERT TO trykatch_platform_runtime
                  WITH CHECK ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    AND NULLIF(current_setting('app.actor_id', true), '')::uuid <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND current_user = 'trykatch_platform_runtime'
                    AND "ActorId" = NULLIF(current_setting('app.actor_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "entitlement_publication_history",
                schema: "platform");
        }
    }
}
