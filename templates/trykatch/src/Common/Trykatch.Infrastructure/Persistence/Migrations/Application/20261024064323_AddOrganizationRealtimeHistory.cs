using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trykatch.Infrastructure.Persistence.Migrations.Application
{
    /// <inheritdoc />
    public partial class AddOrganizationRealtimeHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "realtime_history",
                schema: "app",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    ModuleId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    EventKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyOwnerModuleId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    PolicyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    PolicyVersion = table.Column<int>(type: "integer", nullable: false),
                    AudienceKind = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResourceKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    ResourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_realtime_history", x => new { x.OrganizationId, x.Sequence });
                    table.CheckConstraint("CK_realtime_history_audience", "((\"AudienceKind\" = 0 AND \"UserId\" IS NULL)\n  OR (\"AudienceKind\" = 1 AND \"UserId\" IS NOT NULL AND \"UserId\" <> '00000000-0000-0000-0000-000000000000'::uuid)\n  OR (\"AudienceKind\" = 2 AND \"UserId\" IS NULL AND \"ResourceId\" IS NOT NULL))\nAND ((\"ResourceKey\" IS NULL AND \"ResourceId\" IS NULL)\n  OR (\"ResourceKey\" IS NOT NULL AND \"ResourceId\" IS NOT NULL AND \"ResourceId\" <> '00000000-0000-0000-0000-000000000000'::uuid))");
                    table.CheckConstraint("CK_realtime_history_identity", "\"EventId\" <> '00000000-0000-0000-0000-000000000000'::uuid\nAND \"SourceMessageId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_realtime_history_versions", "\"Sequence\" > 0 AND \"SchemaVersion\" > 0 AND \"PolicyVersion\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "realtime_streams",
                schema: "app",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_realtime_streams", x => x.OrganizationId);
                    table.CheckConstraint("CK_realtime_streams_sequence", "\"Sequence\" > 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_realtime_history_OrganizationId_CreatedAtUtc",
                schema: "app",
                table: "realtime_history",
                columns: new[] { "OrganizationId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_realtime_history_OrganizationId_ModuleId_EventId",
                schema: "app",
                table: "realtime_history",
                columns: new[] { "OrganizationId", "ModuleId", "EventId" },
                unique: true);

            migrationBuilder.Sql("""
                ALTER TABLE app.realtime_streams ENABLE ROW LEVEL SECURITY;
                ALTER TABLE app.realtime_streams FORCE ROW LEVEL SECURITY;
                CREATE POLICY realtime_streams_read ON app.realtime_streams FOR SELECT TO trykatch_org_runtime
                  USING ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid);
                CREATE POLICY realtime_streams_insert ON app.realtime_streams FOR INSERT TO trykatch_org_runtime
                  WITH CHECK ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    AND NULLIF(current_setting('app.actor_id', true), '')::uuid <> '00000000-0000-0000-0000-000000000000'::uuid);
                CREATE POLICY realtime_streams_update ON app.realtime_streams FOR UPDATE TO trykatch_org_runtime
                  USING ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid)
                  WITH CHECK ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    AND NULLIF(current_setting('app.actor_id', true), '')::uuid <> '00000000-0000-0000-0000-000000000000'::uuid);
                ALTER TABLE app.realtime_history ENABLE ROW LEVEL SECURITY;
                ALTER TABLE app.realtime_history FORCE ROW LEVEL SECURITY;
                CREATE POLICY realtime_history_read ON app.realtime_history FOR SELECT TO trykatch_org_runtime
                  USING ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid);
                CREATE POLICY realtime_history_insert ON app.realtime_history FOR INSERT TO trykatch_org_runtime
                  WITH CHECK ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    AND NULLIF(current_setting('app.actor_id', true), '')::uuid <> '00000000-0000-0000-0000-000000000000'::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "realtime_history",
                schema: "app");

            migrationBuilder.DropTable(
                name: "realtime_streams",
                schema: "app");
        }
    }
}
