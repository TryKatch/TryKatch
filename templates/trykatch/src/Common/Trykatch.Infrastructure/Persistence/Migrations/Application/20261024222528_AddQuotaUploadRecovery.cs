using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trykatch.Infrastructure.Persistence.Migrations.Application
{
    /// <inheritdoc />
    public partial class AddQuotaUploadRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UploadPolicyKey",
                schema: "app",
                table: "module_upload_operations",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UploadPolicyVersion",
                schema: "app",
                table: "module_upload_operations",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "quota_maintenance_work",
                schema: "infrastructure",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    ResourceKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ReservationId = table.Column<Guid>(type: "uuid", nullable: true),
                    UploadOperationId = table.Column<Guid>(type: "uuid", nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    DueAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastFailureCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quota_maintenance_work", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("CK_quota_maintenance_work_identity", "\"OrganizationId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid\nAND \"ResourceKey\" ~ '^[a-z][a-z0-9]*([.-][a-z0-9]+)*$'\nAND \"ResourceKey\" !~ '[[:space:]]' AND strpos(\"ResourceKey\", '.') > 0");
                    table.CheckConstraint("CK_quota_maintenance_work_reference", "\"Kind\" IN (1, 2, 3) AND ((\"Kind\" = 1 AND \"ReservationId\" IS NOT NULL AND \"ReservationId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"UploadOperationId\" IS NULL) OR (\"Kind\" = 2 AND \"ReservationId\" IS NOT NULL AND \"ReservationId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"UploadOperationId\" IS NOT NULL AND \"UploadOperationId\" <> '00000000-0000-0000-0000-000000000000'::uuid) OR (\"Kind\" = 3 AND \"ReservationId\" IS NULL AND \"UploadOperationId\" IS NULL))");
                    table.CheckConstraint("CK_quota_maintenance_work_schedule", "isfinite(\"DueAtUtc\") AND isfinite(\"CreatedAtUtc\")\nAND (\"LastFailureCode\" IS NULL OR (\"LastFailureCode\" ~ '^[a-z][a-z0-9_]{0,79}$'\n    AND \"LastFailureCode\" !~ '[[:space:]]'))");
                    table.CheckConstraint("CK_quota_maintenance_work_state", "\"State\" IN (0, 1, 2, 3) AND \"Attempts\" >= 0\nAND ((\"State\" = 1 AND \"LeaseId\" IS NOT NULL AND \"LeaseId\" <>'00000000-0000-0000-0000-000000000000'::uuid AND \"LeaseUntilUtc\" IS NOT NULL AND isfinite(\"LeaseUntilUtc\"))\nOR (\"State\" <> 1 AND \"LeaseId\" IS NULL AND \"LeaseUntilUtc\" IS NULL))");
                    table.ForeignKey(
                        name: "FK_quota_maintenance_work_module_upload_operations_Organizatio~",
                        columns: x => new { x.OrganizationId, x.ResourceKey, x.UploadOperationId },
                        principalSchema: "app",
                        principalTable: "module_upload_operations",
                        principalColumns: new[] { "OrganizationId", "ResourceKey", "OperationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_quota_maintenance_work_organization_entitlements_Organizati~",
                        columns: x => new { x.OrganizationId, x.ResourceKey },
                        principalSchema: "app",
                        principalTable: "organization_entitlements",
                        principalColumns: new[] { "OrganizationId", "ResourceKey" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_quota_maintenance_work_organization_quota_reservations_Orga~",
                        columns: x => new { x.OrganizationId, x.ReservationId },
                        principalSchema: "app",
                        principalTable: "organization_quota_reservations",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_module_upload_operations_policy",
                schema: "app",
                table: "module_upload_operations",
                sql: "(\"UploadPolicyKey\" IS NULL AND \"UploadPolicyVersion\" IS NULL)\nOR (\"UploadPolicyKey\" IS NOT NULL AND \"UploadPolicyVersion\" IS NOT NULL AND \"UploadPolicyVersion\" > 0\n    AND \"UploadPolicyKey\" ~ '^[a-z][a-z0-9]*([.-][a-z0-9]+)*$'\n    AND \"UploadPolicyKey\" !~ '[[:space:]]'\n    AND left(\"UploadPolicyKey\", length(split_part(\"ResourceKey\", '.', 1)) + 1)\n        = split_part(\"ResourceKey\", '.', 1) || '.')");

            migrationBuilder.CreateIndex(
                name: "IX_quota_maintenance_work_OrganizationId_Kind_ResourceKey_Rese~",
                schema: "infrastructure",
                table: "quota_maintenance_work",
                columns: new[] { "OrganizationId", "Kind", "ResourceKey", "ReservationId" },
                unique: true,
                filter: "\"ReservationId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_quota_maintenance_work_OrganizationId_ReservationId",
                schema: "infrastructure",
                table: "quota_maintenance_work",
                columns: new[] { "OrganizationId", "ReservationId" });

            migrationBuilder.CreateIndex(
                name: "IX_quota_maintenance_work_OrganizationId_ResourceKey",
                schema: "infrastructure",
                table: "quota_maintenance_work",
                columns: new[] { "OrganizationId", "ResourceKey" },
                unique: true,
                filter: "\"Kind\" = 3 AND \"State\" IN (0, 1, 2)");

            migrationBuilder.CreateIndex(
                name: "IX_quota_maintenance_work_OrganizationId_ResourceKey_UploadOpe~",
                schema: "infrastructure",
                table: "quota_maintenance_work",
                columns: new[] { "OrganizationId", "ResourceKey", "UploadOperationId" });

            migrationBuilder.CreateIndex(
                name: "IX_quota_maintenance_work_State_DueAtUtc_LeaseUntilUtc_Organiz~",
                schema: "infrastructure",
                table: "quota_maintenance_work",
                columns: new[] { "State", "DueAtUtc", "LeaseUntilUtc", "OrganizationId", "Id" });

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION app.guard_upload_transition()
                RETURNS trigger LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog
                AS $function$
                BEGIN
                  IF TG_OP = 'INSERT' THEN
                    IF NEW."UploadPolicyKey" IS NULL OR NEW."UploadPolicyVersion" IS NULL THEN
                      RAISE EXCEPTION 'New uploads require an explicit policy identity.' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                  END IF;
                  IF ROW(NEW."OrganizationId", NEW."ResourceKey", NEW."OperationId", NEW."ActorId", NEW."MembershipId",
                         NEW."ReservationId", NEW."SubjectId", NEW."ObjectKey", NEW."StorageNamespaceFingerprint",
                         NEW."Length", NEW."Sha256", NEW."MetadataFingerprint", NEW."UploadPolicyKey", NEW."UploadPolicyVersion",
                         NEW."CreatedAtUtc", NEW."ExpiresAtUtc")
                     IS DISTINCT FROM ROW(OLD."OrganizationId", OLD."ResourceKey", OLD."OperationId", OLD."ActorId", OLD."MembershipId",
                         OLD."ReservationId", OLD."SubjectId", OLD."ObjectKey", OLD."StorageNamespaceFingerprint",
                         OLD."Length", OLD."Sha256", OLD."MetadataFingerprint", OLD."UploadPolicyKey", OLD."UploadPolicyVersion",
                         OLD."CreatedAtUtc", OLD."ExpiresAtUtc")
                     OR (OLD."State" = 0 AND NEW."State" NOT IN (0, 1, 2))
                     OR (OLD."State" = 2 AND NEW."State" NOT IN (2, 3))
                     OR (OLD."State" IN (1, 3) AND ROW(NEW."State", NEW."ClosedAtUtc")
                         IS DISTINCT FROM ROW(OLD."State", OLD."ClosedAtUtc")) THEN
                    RAISE EXCEPTION 'Upload identity and fenced or closed state cannot be reversed.' USING ERRCODE = '23514';
                  END IF;
                  RETURN NEW;
                END
                $function$;
                REVOKE ALL ON FUNCTION app.guard_upload_transition() FROM PUBLIC;
                DROP TRIGGER guard_upload_transition ON app.module_upload_operations;
                CREATE TRIGGER guard_upload_transition BEFORE INSERT OR UPDATE ON app.module_upload_operations
                FOR EACH ROW EXECUTE FUNCTION app.guard_upload_transition();
                CREATE OR REPLACE FUNCTION infrastructure.guard_quota_maintenance_transition()
                RETURNS trigger LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog
                AS $function$
                BEGIN
                  IF ROW(NEW."OrganizationId", NEW."Id", NEW."Kind", NEW."ResourceKey", NEW."ReservationId",
                         NEW."UploadOperationId", NEW."CreatedAtUtc")
                     IS DISTINCT FROM ROW(OLD."OrganizationId", OLD."Id", OLD."Kind", OLD."ResourceKey", OLD."ReservationId",
                         OLD."UploadOperationId", OLD."CreatedAtUtc")
                     OR NEW."Attempts" < OLD."Attempts"
                     OR (OLD."State" = 3 AND NEW IS DISTINCT FROM OLD) THEN
                    RAISE EXCEPTION 'Quota recovery references and completed work are immutable.' USING ERRCODE = '23514';
                  END IF;
                  RETURN NEW;
                END
                $function$;
                REVOKE ALL ON FUNCTION infrastructure.guard_quota_maintenance_transition() FROM PUBLIC;
                CREATE TRIGGER guard_quota_maintenance_transition BEFORE UPDATE ON infrastructure.quota_maintenance_work
                FOR EACH ROW EXECUTE FUNCTION infrastructure.guard_quota_maintenance_transition();
                ALTER TABLE infrastructure.quota_maintenance_work ENABLE ROW LEVEL SECURITY;
                ALTER TABLE infrastructure.quota_maintenance_work FORCE ROW LEVEL SECURITY;
                CREATE POLICY quota_maintenance_work_append ON infrastructure.quota_maintenance_work FOR INSERT TO trykatch_org_runtime
                WITH CHECK ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    AND NULLIF(current_setting('app.actor_id', true), '')::uuid <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND "State" = 0 AND "Attempts" = 0 AND "LeaseId" IS NULL AND "LeaseUntilUtc" IS NULL AND "LastFailureCode" IS NULL);
                CREATE POLICY quota_maintenance_work_read ON infrastructure.quota_maintenance_work FOR SELECT TO trykatch_work_coordinator
                USING (current_user = 'trykatch_work_coordinator');
                CREATE POLICY quota_maintenance_work_update ON infrastructure.quota_maintenance_work FOR UPDATE TO trykatch_work_coordinator
                USING (current_user = 'trykatch_work_coordinator') WITH CHECK (current_user = 'trykatch_work_coordinator');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER guard_quota_maintenance_transition ON infrastructure.quota_maintenance_work;
                DROP FUNCTION infrastructure.guard_quota_maintenance_transition();
                DROP TRIGGER guard_upload_transition ON app.module_upload_operations;
                CREATE OR REPLACE FUNCTION app.guard_upload_transition()
                RETURNS trigger LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog
                AS $function$
                BEGIN
                  IF ROW(NEW."OrganizationId", NEW."ResourceKey", NEW."OperationId", NEW."ActorId", NEW."MembershipId",
                         NEW."ReservationId", NEW."SubjectId", NEW."ObjectKey", NEW."StorageNamespaceFingerprint",
                         NEW."Length", NEW."Sha256", NEW."MetadataFingerprint", NEW."CreatedAtUtc", NEW."ExpiresAtUtc")
                     IS DISTINCT FROM ROW(OLD."OrganizationId", OLD."ResourceKey", OLD."OperationId", OLD."ActorId", OLD."MembershipId",
                         OLD."ReservationId", OLD."SubjectId", OLD."ObjectKey", OLD."StorageNamespaceFingerprint",
                         OLD."Length", OLD."Sha256", OLD."MetadataFingerprint", OLD."CreatedAtUtc", OLD."ExpiresAtUtc")
                     OR (OLD."State" = 0 AND NEW."State" NOT IN (0, 1, 2))
                     OR (OLD."State" = 2 AND NEW."State" NOT IN (2, 3))
                     OR (OLD."State" IN (1, 3) AND ROW(NEW."State", NEW."ClosedAtUtc")
                         IS DISTINCT FROM ROW(OLD."State", OLD."ClosedAtUtc")) THEN
                    RAISE EXCEPTION 'Upload identity and fenced or closed state cannot be reversed.' USING ERRCODE = '23514';
                  END IF;
                  RETURN NEW;
                END
                $function$;
                REVOKE ALL ON FUNCTION app.guard_upload_transition() FROM PUBLIC;
                CREATE TRIGGER guard_upload_transition BEFORE UPDATE ON app.module_upload_operations
                FOR EACH ROW EXECUTE FUNCTION app.guard_upload_transition();
                """);

            migrationBuilder.DropTable(
                name: "quota_maintenance_work",
                schema: "infrastructure");

            migrationBuilder.DropCheckConstraint(
                name: "CK_module_upload_operations_policy",
                schema: "app",
                table: "module_upload_operations");

            migrationBuilder.DropColumn(
                name: "UploadPolicyKey",
                schema: "app",
                table: "module_upload_operations");

            migrationBuilder.DropColumn(
                name: "UploadPolicyVersion",
                schema: "app",
                table: "module_upload_operations");
        }
    }
}
