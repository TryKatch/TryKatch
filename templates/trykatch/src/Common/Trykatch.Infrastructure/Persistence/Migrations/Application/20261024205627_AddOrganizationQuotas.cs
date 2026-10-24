using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trykatch.Infrastructure.Persistence.Migrations.Application
{
    /// <inheritdoc />
    public partial class AddOrganizationQuotas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "organization_entitlements",
                schema: "app",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    OwnerModuleId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Unit = table.Column<int>(type: "integer", nullable: false),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    Limit = table.Column<long>(type: "bigint", nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    SourceRevision = table.Column<long>(type: "bigint", nullable: false),
                    SourceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EffectiveAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_entitlements", x => new { x.OrganizationId, x.ResourceKey });
                    table.CheckConstraint("CK_organization_entitlements_identity", "\"OrganizationId\" <> '00000000-0000-0000-0000-000000000000'::uuid\nAND \"ResourceKey\" ~ '^[a-z][a-z0-9]*([.-][a-z0-9]+)*$'\nAND \"ResourceKey\" !~ '[[:space:]]' AND strpos(\"ResourceKey\", '.') > 0\nAND \"OwnerModuleId\" ~ '^[a-z][a-z0-9]*(-[a-z0-9]+)*$'\nAND \"OwnerModuleId\" !~ '[[:space:]]'\nAND left(\"ResourceKey\", length(\"OwnerModuleId\") + 1) = \"OwnerModuleId\" || '.'\nAND \"Unit\" IN (1, 2)");
                    table.CheckConstraint("CK_organization_entitlements_projection", "(\"Mode\" = 1 AND \"Limit\" IS NOT NULL AND \"Limit\" >= 0)\nOR (\"Mode\" IN (0, 2, 3) AND \"Limit\" IS NULL)");
                    table.CheckConstraint("CK_organization_entitlements_source", "\"Version\" <> '00000000-0000-0000-0000-000000000000'::uuid\nAND \"SourceRevision\" >= 0 AND \"SourceHash\" ~ '^[A-Fa-f0-9]{64}$'\nAND length(btrim(\"Source\")) > 0 AND isfinite(\"EffectiveAtUtc\")");
                });

            migrationBuilder.CreateTable(
                name: "organization_quota_inventory",
                schema: "app",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    SourceKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    SourceVersion = table.Column<int>(type: "integer", nullable: true),
                    Cursor = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_quota_inventory", x => new { x.OrganizationId, x.ResourceKey });
                    table.CheckConstraint("CK_organization_quota_inventory_identity", "\"OrganizationId\" <> '00000000-0000-0000-0000-000000000000'::uuid\nAND \"ResourceKey\" ~ '^[a-z][a-z0-9]*([.-][a-z0-9]+)*$'\nAND \"ResourceKey\" !~ '[[:space:]]' AND strpos(\"ResourceKey\", '.') > 0");
                    table.CheckConstraint("CK_organization_quota_inventory_progress", "\"State\" IN (0, 1, 2, 3) AND isfinite(\"UpdatedAtUtc\")\nAND ((\"SourceKey\" IS NULL AND \"SourceVersion\" IS NULL AND \"State\" <> 1)\n    OR (\"SourceKey\" IS NOT NULL AND \"SourceVersion\" IS NOT NULL AND \"SourceVersion\" > 0\n        AND \"SourceKey\" ~ '^[a-z][a-z0-9]*([.-][a-z0-9]+)*$'\n        AND \"SourceKey\" !~ '[[:space:]]'))");
                    table.ForeignKey(
                        name: "FK_organization_quota_inventory_organization_entitlements_Orga~",
                        columns: x => new { x.OrganizationId, x.ResourceKey },
                        principalSchema: "app",
                        principalTable: "organization_entitlements",
                        principalColumns: new[] { "OrganizationId", "ResourceKey" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "organization_quota_reservations",
                schema: "app",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Units = table.Column<long>(type: "bigint", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntitlementVersion = table.Column<Guid>(type: "uuid", nullable: false),
                    EffectKind = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClosedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_quota_reservations", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("CK_organization_quota_reservations_identity", "\"OrganizationId\" <> '00000000-0000-0000-0000-000000000000'::uuid\nAND \"ResourceKey\" ~ '^[a-z][a-z0-9]*([.-][a-z0-9]+)*$'\nAND \"ResourceKey\" !~ '[[:space:]]' AND strpos(\"ResourceKey\", '.') > 0 AND \"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"ActorId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"OperationId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"EntitlementVersion\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_organization_quota_reservations_shape", "\"Units\" > 0 AND \"EffectKind\" IN (1, 2) AND \"State\" IN (0, 1, 2)\nAND isfinite(\"CreatedAtUtc\") AND isfinite(\"ExpiresAtUtc\") AND \"ExpiresAtUtc\" > \"CreatedAtUtc\"\nAND ((\"State\" = 0 AND \"ClosedAtUtc\" IS NULL) OR\n    (\"State\" IN (1, 2) AND \"ClosedAtUtc\" IS NOT NULL\n        AND isfinite(\"ClosedAtUtc\") AND \"ClosedAtUtc\" >= \"CreatedAtUtc\"))");
                    table.ForeignKey(
                        name: "FK_organization_quota_reservations_organization_entitlements_O~",
                        columns: x => new { x.OrganizationId, x.ResourceKey },
                        principalSchema: "app",
                        principalTable: "organization_entitlements",
                        principalColumns: new[] { "OrganizationId", "ResourceKey" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "module_upload_operations",
                schema: "app",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    MembershipId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReservationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ObjectKey = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    StorageNamespaceFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Length = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MetadataFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClosedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_module_upload_operations", x => new { x.OrganizationId, x.ResourceKey, x.OperationId });
                    table.CheckConstraint("CK_module_upload_operations_identity", "\"OrganizationId\" <> '00000000-0000-0000-0000-000000000000'::uuid\nAND \"ResourceKey\" ~ '^[a-z][a-z0-9]*([.-][a-z0-9]+)*$'\nAND \"ResourceKey\" !~ '[[:space:]]' AND strpos(\"ResourceKey\", '.') > 0 AND \"OperationId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"ActorId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"MembershipId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"ReservationId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"SubjectId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_module_upload_operations_payload", "\"Length\" > 0 AND length(\"ObjectKey\") > 0\nAND \"Sha256\" ~ '^[A-Fa-f0-9]{64}$' AND \"MetadataFingerprint\" ~ '^[A-Fa-f0-9]{64}$'\nAND \"StorageNamespaceFingerprint\" ~ '^[A-Fa-f0-9]{64}$'");
                    table.CheckConstraint("CK_module_upload_operations_state", "\"State\" IN (0, 1, 2, 3) AND isfinite(\"CreatedAtUtc\") AND isfinite(\"ExpiresAtUtc\")\nAND \"ExpiresAtUtc\" > \"CreatedAtUtc\"\nAND ((\"State\" IN (0, 2) AND \"ClosedAtUtc\" IS NULL) OR\n    (\"State\" IN (1, 3) AND \"ClosedAtUtc\" IS NOT NULL\n        AND isfinite(\"ClosedAtUtc\") AND \"ClosedAtUtc\" >= \"CreatedAtUtc\"))");
                    table.ForeignKey(
                        name: "FK_module_upload_operations_organization_entitlements_Organiza~",
                        columns: x => new { x.OrganizationId, x.ResourceKey },
                        principalSchema: "app",
                        principalTable: "organization_entitlements",
                        principalColumns: new[] { "OrganizationId", "ResourceKey" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_module_upload_operations_organization_quota_reservations_Or~",
                        columns: x => new { x.OrganizationId, x.ReservationId },
                        principalSchema: "app",
                        principalTable: "organization_quota_reservations",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "organization_usage",
                schema: "app",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    EffectId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceEffectId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReservationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Units = table.Column<long>(type: "bigint", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_usage", x => new { x.OrganizationId, x.ResourceKey, x.EffectId });
                    table.CheckConstraint("CK_organization_usage_effect", "\"Units\" <> 0 AND \"Units\" <> '-9223372036854775808'::bigint\nAND isfinite(\"OccurredAtUtc\")\nAND ((\"Units\" > 0 AND \"SourceEffectId\" IS NULL)\n    OR (\"Units\" < 0 AND \"SourceEffectId\" IS NOT NULL AND \"SourceEffectId\" <> \"EffectId\"))");
                    table.CheckConstraint("CK_organization_usage_identity", "\"OrganizationId\" <> '00000000-0000-0000-0000-000000000000'::uuid\nAND \"ResourceKey\" ~ '^[a-z][a-z0-9]*([.-][a-z0-9]+)*$'\nAND \"ResourceKey\" !~ '[[:space:]]' AND strpos(\"ResourceKey\", '.') > 0 AND \"EffectId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"SourceId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"ActorId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_organization_usage_organization_entitlements_OrganizationId~",
                        columns: x => new { x.OrganizationId, x.ResourceKey },
                        principalSchema: "app",
                        principalTable: "organization_entitlements",
                        principalColumns: new[] { "OrganizationId", "ResourceKey" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_organization_usage_organization_quota_reservations_Organiza~",
                        columns: x => new { x.OrganizationId, x.ReservationId },
                        principalSchema: "app",
                        principalTable: "organization_quota_reservations",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_organization_usage_organization_usage_OrganizationId_Resour~",
                        columns: x => new { x.OrganizationId, x.ResourceKey, x.SourceEffectId },
                        principalSchema: "app",
                        principalTable: "organization_usage",
                        principalColumns: new[] { "OrganizationId", "ResourceKey", "EffectId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_module_upload_operations_OrganizationId_ObjectKey",
                schema: "app",
                table: "module_upload_operations",
                columns: new[] { "OrganizationId", "ObjectKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_module_upload_operations_OrganizationId_ReservationId",
                schema: "app",
                table: "module_upload_operations",
                columns: new[] { "OrganizationId", "ReservationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_module_upload_operations_OrganizationId_SubjectId",
                schema: "app",
                table: "module_upload_operations",
                columns: new[] { "OrganizationId", "SubjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_organization_quota_reservations_OrganizationId_ResourceKey_~",
                schema: "app",
                table: "organization_quota_reservations",
                columns: new[] { "OrganizationId", "ResourceKey", "OperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_organization_quota_reservations_OrganizationId_ResourceKey~1",
                schema: "app",
                table: "organization_quota_reservations",
                columns: new[] { "OrganizationId", "ResourceKey", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_organization_usage_OrganizationId_ReservationId",
                schema: "app",
                table: "organization_usage",
                columns: new[] { "OrganizationId", "ReservationId" });

            migrationBuilder.CreateIndex(
                name: "IX_organization_usage_OrganizationId_ResourceKey_SourceEffectId",
                schema: "app",
                table: "organization_usage",
                columns: new[] { "OrganizationId", "ResourceKey", "SourceEffectId" });

            migrationBuilder.Sql("""
                CREATE FUNCTION app.guard_entitlement_identity()
                RETURNS trigger LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog
                AS $function$
                BEGIN
                  IF ROW(NEW."OrganizationId", NEW."ResourceKey", NEW."OwnerModuleId", NEW."Unit")
                     IS DISTINCT FROM ROW(OLD."OrganizationId", OLD."ResourceKey", OLD."OwnerModuleId", OLD."Unit") THEN
                    RAISE EXCEPTION 'Quota resource identity is immutable.' USING ERRCODE = '23514';
                  END IF;
                  RETURN NEW;
                END
                $function$;
                REVOKE ALL ON FUNCTION app.guard_entitlement_identity() FROM PUBLIC;
                CREATE TRIGGER guard_entitlement_identity BEFORE UPDATE ON app.organization_entitlements
                FOR EACH ROW EXECUTE FUNCTION app.guard_entitlement_identity();

                CREATE FUNCTION app.guard_quota_reservation_transition()
                RETURNS trigger LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog
                AS $function$
                BEGIN
                  IF ROW(NEW."OrganizationId", NEW."Id", NEW."ResourceKey", NEW."ActorId", NEW."Units", NEW."OperationId",
                         NEW."EntitlementVersion", NEW."EffectKind", NEW."CreatedAtUtc", NEW."ExpiresAtUtc")
                     IS DISTINCT FROM ROW(OLD."OrganizationId", OLD."Id", OLD."ResourceKey", OLD."ActorId", OLD."Units", OLD."OperationId",
                         OLD."EntitlementVersion", OLD."EffectKind", OLD."CreatedAtUtc", OLD."ExpiresAtUtc")
                     OR (OLD."State" IN (1, 2) AND ROW(NEW."State", NEW."ClosedAtUtc")
                         IS DISTINCT FROM ROW(OLD."State", OLD."ClosedAtUtc")) THEN
                    RAISE EXCEPTION 'Quota reservation identity and closed state are immutable.' USING ERRCODE = '23514';
                  END IF;
                  RETURN NEW;
                END
                $function$;
                REVOKE ALL ON FUNCTION app.guard_quota_reservation_transition() FROM PUBLIC;
                CREATE TRIGGER guard_quota_reservation_transition BEFORE UPDATE ON app.organization_quota_reservations
                FOR EACH ROW EXECUTE FUNCTION app.guard_quota_reservation_transition();

                CREATE FUNCTION app.guard_upload_transition()
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

            const string scope = """
                "OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid
                AND NULLIF(current_setting('app.actor_id', true), '')::uuid <> '00000000-0000-0000-0000-000000000000'::uuid
                """;
            foreach (string table in new[]
                {
                    "organization_entitlements", "organization_quota_reservations", "organization_usage",
                    "organization_quota_inventory", "module_upload_operations"
                })
            {
                migrationBuilder.Sql($"""
                    ALTER TABLE app.{table} ENABLE ROW LEVEL SECURITY;
                    ALTER TABLE app.{table} FORCE ROW LEVEL SECURITY;
                    CREATE POLICY {table}_read ON app.{table} FOR SELECT TO trykatch_org_runtime
                        USING ({scope});
                    """);
                if (table == "organization_entitlements") continue;
                migrationBuilder.Sql($"""
                    CREATE POLICY {table}_insert ON app.{table} FOR INSERT TO trykatch_org_runtime
                        WITH CHECK ({scope});
                    """);
                if (table == "organization_usage") continue;
                migrationBuilder.Sql($"""
                    CREATE POLICY {table}_update ON app.{table} FOR UPDATE TO trykatch_org_runtime
                        USING ({scope}) WITH CHECK ({scope});
                    """);
            }
            const string platform = scope + " AND current_user = 'trykatch_platform_runtime'";
            migrationBuilder.Sql($"""
                CREATE POLICY organization_entitlements_platform_read ON app.organization_entitlements
                    FOR SELECT TO trykatch_platform_runtime USING ({platform});
                CREATE POLICY organization_entitlements_platform_insert ON app.organization_entitlements
                    FOR INSERT TO trykatch_platform_runtime WITH CHECK ({platform});
                CREATE POLICY organization_entitlements_platform_update ON app.organization_entitlements
                    FOR UPDATE TO trykatch_platform_runtime USING ({platform}) WITH CHECK ({platform});
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "module_upload_operations",
                schema: "app");

            migrationBuilder.DropTable(
                name: "organization_quota_inventory",
                schema: "app");

            migrationBuilder.DropTable(
                name: "organization_usage",
                schema: "app");

            migrationBuilder.DropTable(
                name: "organization_quota_reservations",
                schema: "app");

            migrationBuilder.DropTable(
                name: "organization_entitlements",
                schema: "app");

            migrationBuilder.Sql("""
                DROP FUNCTION app.guard_entitlement_identity();
                DROP FUNCTION app.guard_quota_reservation_transition();
                DROP FUNCTION app.guard_upload_transition();
                """);
        }
    }
}
