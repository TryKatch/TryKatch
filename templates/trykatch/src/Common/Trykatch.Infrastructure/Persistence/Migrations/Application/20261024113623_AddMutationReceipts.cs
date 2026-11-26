using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trykatch.Infrastructure.Persistence.Migrations.Application
{
    /// <inheritdoc />
    public partial class AddMutationReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mutation_receipts",
                schema: "app",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModuleId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    OperationId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    KeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StatusCode = table.Column<int>(type: "integer", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Location = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Body = table.Column<byte[]>(type: "bytea", nullable: false),
                    ResourceKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    ResourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mutation_receipts", x => new { x.OrganizationId, x.ActorId, x.ModuleId, x.OperationId, x.KeyHash });
                    table.CheckConstraint("CK_mutation_receipts_identity", "\"ModuleId\" ~ '^[A-Za-z0-9][A-Za-z0-9._:-]*$'\nAND \"OperationId\" ~ '^[A-Za-z0-9][A-Za-z0-9._:-]*$'\nAND \"KeyHash\" ~ '^[0-9A-F]{64}$' AND \"RequestHash\" ~ '^[0-9A-F]{64}$'");
                    table.CheckConstraint("CK_mutation_receipts_lifetime", "\"ExpiresAtUtc\" >= \"CompletedAtUtc\" + interval '1 minute'\nAND \"ExpiresAtUtc\" <= \"CompletedAtUtc\" + interval '24 hours'");
                    table.CheckConstraint("CK_mutation_receipts_principal", "\"OrganizationId\" <> '00000000-0000-0000-0000-000000000000'::uuid\nAND \"ActorId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_mutation_receipts_resource", "(\"ResourceKey\" IS NULL AND \"ResourceId\" IS NULL)\nOR (\"ResourceKey\" IS NOT NULL AND \"ResourceId\" IS NOT NULL\n    AND \"ResourceKey\" ~ '^[A-Za-z0-9][A-Za-z0-9._:-]*$'\n    AND \"ResourceId\" <> '00000000-0000-0000-0000-000000000000'::uuid)");
                    table.CheckConstraint("CK_mutation_receipts_response", "\"StatusCode\" BETWEEN 200 AND 299 AND octet_length(\"Body\") <= 1048576\nAND (\"ContentType\" IS NULL OR (length(\"ContentType\") > 0 AND \"ContentType\" !~ '[[:cntrl:]]'))\nAND (\"Location\" IS NULL OR (left(\"Location\", 1) = '/' AND left(\"Location\", 2) <> '//'\n    AND strpos(\"Location\", chr(92)) = 0 AND \"Location\" !~ '[[:cntrl:]]'))");
                });

            migrationBuilder.CreateIndex(
                name: "IX_mutation_receipts_OrganizationId_ExpiresAtUtc",
                schema: "app",
                table: "mutation_receipts",
                columns: new[] { "OrganizationId", "ExpiresAtUtc" });

            migrationBuilder.Sql("""
                ALTER TABLE app.mutation_receipts ENABLE ROW LEVEL SECURITY;
                ALTER TABLE app.mutation_receipts FORCE ROW LEVEL SECURITY;
                CREATE POLICY mutation_receipts_read ON app.mutation_receipts FOR SELECT TO trykatch_org_runtime
                  USING ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    AND "ActorId" = NULLIF(current_setting('app.actor_id', true), '')::uuid);
                CREATE POLICY mutation_receipts_insert ON app.mutation_receipts FOR INSERT TO trykatch_org_runtime
                  WITH CHECK ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    AND "ActorId" = NULLIF(current_setting('app.actor_id', true), '')::uuid);
                CREATE POLICY mutation_receipts_update ON app.mutation_receipts FOR UPDATE TO trykatch_org_runtime
                  USING ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    AND "ActorId" = NULLIF(current_setting('app.actor_id', true), '')::uuid)
                  WITH CHECK ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    AND "ActorId" = NULLIF(current_setting('app.actor_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mutation_receipts",
                schema: "app");
        }
    }
}
