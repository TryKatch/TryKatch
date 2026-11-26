using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trykatch.Infrastructure.Persistence.Migrations.Application
{
    /// <inheritdoc />
    public partial class AddOrganizationCacheInvalidations : Migration
    {
        private static readonly string[] OrganizationUpdatedIndex = ["OrganizationId", "UpdatedAtUtc"];
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cache_invalidations",
                schema: "app",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    KeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Generation = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cache_invalidations", x => new { x.OrganizationId, x.KeyHash });
                    table.CheckConstraint("CK_cache_invalidations_generation", "\"Generation\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_cache_invalidations_hash", "\"KeyHash\" ~ '^[0-9a-f]{64}$'");
                });

            migrationBuilder.CreateIndex(
                name: "IX_cache_invalidations_OrganizationId_UpdatedAtUtc",
                schema: "app",
                table: "cache_invalidations",
                columns: OrganizationUpdatedIndex);

            migrationBuilder.Sql("""
                ALTER TABLE app.cache_invalidations ENABLE ROW LEVEL SECURITY;
                ALTER TABLE app.cache_invalidations FORCE ROW LEVEL SECURITY;
                CREATE POLICY cache_invalidations_read ON app.cache_invalidations FOR SELECT TO trykatch_org_runtime
                  USING ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid);
                CREATE POLICY cache_invalidations_insert ON app.cache_invalidations FOR INSERT TO trykatch_org_runtime
                  WITH CHECK ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    AND NULLIF(current_setting('app.actor_id', true), '')::uuid <> '00000000-0000-0000-0000-000000000000'::uuid);
                CREATE POLICY cache_invalidations_update ON app.cache_invalidations FOR UPDATE TO trykatch_org_runtime
                  USING ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid)
                  WITH CHECK ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    AND NULLIF(current_setting('app.actor_id', true), '')::uuid <> '00000000-0000-0000-0000-000000000000'::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cache_invalidations",
                schema: "app");
        }
    }
}
