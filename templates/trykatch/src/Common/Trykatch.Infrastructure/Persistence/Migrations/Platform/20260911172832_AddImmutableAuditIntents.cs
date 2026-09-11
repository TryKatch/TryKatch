using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trykatch.Infrastructure.Persistence.Migrations.Platform
{
    /// <inheritdoc />
    public partial class AddImmutableAuditIntents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_intents",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Operation = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    SubjectType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    SubjectId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    SubjectDisplayName = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    Details = table.Column<string>(type: "jsonb", maxLength: 2048, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_intents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_intents_OrganizationId_OccurredAt_Id",
                schema: "platform",
                table: "audit_intents",
                columns: new[] { "OrganizationId", "OccurredAt", "Id" });

            migrationBuilder.Sql("""
                ALTER TABLE platform.audit_intents
                  ADD CONSTRAINT ck_audit_intents_nonempty_ids
                    CHECK ("Id" <> '00000000-0000-0000-0000-000000000000'::uuid
                      AND "OrganizationId" <> '00000000-0000-0000-0000-000000000000'::uuid
                      AND "ActorId" <> '00000000-0000-0000-0000-000000000000'::uuid),
                  ADD CONSTRAINT ck_audit_intents_details_size
                    CHECK (octet_length("Details"::text) <= 2048),
                  ADD CONSTRAINT ck_audit_intents_details_allowlist
                    CHECK (("Details" - 'expiresAt' - 'permissionCount' - 'reasonProvided' - 'roleCount' - 'status') = '{}'::jsonb),
                  ADD CONSTRAINT ck_audit_intents_details_types
                    CHECK (jsonb_typeof("Details") = 'object'
                      AND NOT jsonb_path_exists("Details", '$.* ? (@.type() != "string" && @.type() != "null")'));

                ALTER TABLE platform.audit_intents ENABLE ROW LEVEL SECURITY;
                ALTER TABLE platform.audit_intents FORCE ROW LEVEL SECURITY;
                CREATE POLICY audit_intents_append ON platform.audit_intents FOR INSERT TO trykatch_org_runtime
                  WITH CHECK (
                    "OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    AND "ActorId" = NULLIF(current_setting('app.actor_id', true), '')::uuid);
                CREATE POLICY audit_intents_worker_read ON platform.audit_intents FOR SELECT TO trykatch_outbox_worker
                  USING (current_user = 'trykatch_outbox_worker');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS audit_intents_worker_read ON platform.audit_intents;
                DROP POLICY IF EXISTS audit_intents_append ON platform.audit_intents;
                """);
            migrationBuilder.DropTable(
                name: "audit_intents",
                schema: "platform");
        }
    }
}
