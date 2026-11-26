using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trykatch.Infrastructure.Persistence.Migrations.Platform
{
    /// <inheritdoc />
    public partial class AddFeatureAvailabilityControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "feature_control_history",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Environment = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FeatureKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreviousVersion = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    PreviousEnabled = table.Column<bool>(type: "boolean", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: true),
                    PreviousForceDisabled = table.Column<bool>(type: "boolean", nullable: true),
                    ForceDisabled = table.Column<bool>(type: "boolean", nullable: true),
                    ChangedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feature_control_history", x => x.Id);
                    table.CheckConstraint("CK_feature_control_history_identity", "\"Environment\" ~ '^[a-z0-9][a-z0-9._-]{0,63}$'\nAND \"FeatureKey\" ~ '^[a-z][a-z0-9]*([.-][a-z0-9]+)*$'\nAND strpos(\"FeatureKey\", '.') > 0");
                    table.CheckConstraint("CK_feature_control_history_principals", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid\nAND \"ActorId\" <> '00000000-0000-0000-0000-000000000000'::uuid\nAND (\"OrganizationId\" IS NULL OR \"OrganizationId\" <> '00000000-0000-0000-0000-000000000000'::uuid)");
                    table.CheckConstraint("CK_feature_control_history_shape", "(\"PreviousVersion\" IS NOT NULL OR \"PreviousEnabled\" IS NULL)\nAND ((\"OrganizationId\" IS NULL AND \"ForceDisabled\" IS NOT NULL\n    AND ((\"PreviousVersion\" IS NULL AND \"PreviousForceDisabled\" IS NULL)\n        OR (\"PreviousVersion\" IS NOT NULL AND \"PreviousForceDisabled\" IS NOT NULL)))\n    OR (\"OrganizationId\" IS NOT NULL AND \"ForceDisabled\" IS NULL AND \"PreviousForceDisabled\" IS NULL))");
                    table.CheckConstraint("CK_feature_control_history_time", "isfinite(\"ChangedAtUtc\")");
                    table.CheckConstraint("CK_feature_control_history_versions", "\"Version\" <> '00000000-0000-0000-0000-000000000000'::uuid\nAND (\"PreviousVersion\" IS NULL OR\n    (\"PreviousVersion\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"PreviousVersion\" <> \"Version\"))");
                    table.ForeignKey(
                        name: "FK_feature_control_history_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "platform",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "feature_environment_controls",
                schema: "platform",
                columns: table => new
                {
                    Environment = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FeatureKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    DefaultEnabled = table.Column<bool>(type: "boolean", nullable: true),
                    ForceDisabled = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feature_environment_controls", x => new { x.Environment, x.FeatureKey });
                    table.CheckConstraint("CK_feature_environment_controls_identity", "\"Environment\" ~ '^[a-z0-9][a-z0-9._-]{0,63}$'\nAND \"FeatureKey\" ~ '^[a-z][a-z0-9]*([.-][a-z0-9]+)*$'\nAND strpos(\"FeatureKey\", '.') > 0");
                    table.CheckConstraint("CK_feature_environment_controls_time", "isfinite(\"UpdatedAtUtc\")");
                    table.CheckConstraint("CK_feature_environment_controls_version", "\"Version\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                });

            migrationBuilder.CreateTable(
                name: "organization_feature_overrides",
                schema: "platform",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Environment = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FeatureKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_feature_overrides", x => new { x.OrganizationId, x.Environment, x.FeatureKey });
                    table.CheckConstraint("CK_organization_feature_overrides_identity", "\"Environment\" ~ '^[a-z0-9][a-z0-9._-]{0,63}$'\nAND \"FeatureKey\" ~ '^[a-z][a-z0-9]*([.-][a-z0-9]+)*$'\nAND strpos(\"FeatureKey\", '.') > 0");
                    table.CheckConstraint("CK_organization_feature_overrides_organization", "\"OrganizationId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_organization_feature_overrides_time", "isfinite(\"UpdatedAtUtc\")");
                    table.CheckConstraint("CK_organization_feature_overrides_version", "\"Version\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_organization_feature_overrides_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "platform",
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_feature_control_history_Environment_FeatureKey_Organization~",
                schema: "platform",
                table: "feature_control_history",
                columns: new[] { "Environment", "FeatureKey", "OrganizationId", "ChangedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_feature_control_history_OrganizationId",
                schema: "platform",
                table: "feature_control_history",
                column: "OrganizationId");
            migrationBuilder.Sql("""
                ALTER TABLE platform.feature_environment_controls ENABLE ROW LEVEL SECURITY;
                ALTER TABLE platform.feature_environment_controls FORCE ROW LEVEL SECURITY;
                CREATE POLICY feature_environment_controls_read ON platform.feature_environment_controls FOR SELECT TO trykatch_org_runtime
                  USING (NULLIF(current_setting('app.actor_id', true), '')::uuid <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND NULLIF(current_setting('app.organization_id', true), '')::uuid <> '00000000-0000-0000-0000-000000000000'::uuid);
                CREATE POLICY feature_environment_controls_platform_read ON platform.feature_environment_controls FOR SELECT TO trykatch_platform_runtime
                  USING (current_user = 'trykatch_platform_runtime'
                    AND NULLIF(current_setting('app.actor_id', true), '')::uuid <> '00000000-0000-0000-0000-000000000000'::uuid);
                CREATE POLICY feature_environment_controls_insert ON platform.feature_environment_controls FOR INSERT TO trykatch_platform_runtime
                  WITH CHECK (current_user = 'trykatch_platform_runtime'
                    AND NULLIF(current_setting('app.actor_id', true), '')::uuid <> '00000000-0000-0000-0000-000000000000'::uuid);
                CREATE POLICY feature_environment_controls_update ON platform.feature_environment_controls FOR UPDATE TO trykatch_platform_runtime
                  USING (current_user = 'trykatch_platform_runtime'
                    AND NULLIF(current_setting('app.actor_id', true), '')::uuid <> '00000000-0000-0000-0000-000000000000'::uuid)
                  WITH CHECK (current_user = 'trykatch_platform_runtime'
                    AND NULLIF(current_setting('app.actor_id', true), '')::uuid <> '00000000-0000-0000-0000-000000000000'::uuid);

                ALTER TABLE platform.organization_feature_overrides ENABLE ROW LEVEL SECURITY;
                ALTER TABLE platform.organization_feature_overrides FORCE ROW LEVEL SECURITY;
                CREATE POLICY organization_feature_overrides_read ON platform.organization_feature_overrides FOR SELECT TO trykatch_org_runtime
                  USING ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    AND NULLIF(current_setting('app.actor_id', true), '')::uuid <> '00000000-0000-0000-0000-000000000000'::uuid);
                CREATE POLICY organization_feature_overrides_platform_read ON platform.organization_feature_overrides FOR SELECT TO trykatch_platform_runtime
                  USING (current_user = 'trykatch_platform_runtime'
                    AND NULLIF(current_setting('app.actor_id', true), '')::uuid <> '00000000-0000-0000-0000-000000000000'::uuid);
                CREATE POLICY organization_feature_overrides_insert ON platform.organization_feature_overrides FOR INSERT TO trykatch_platform_runtime
                  WITH CHECK (current_user = 'trykatch_platform_runtime'
                    AND NULLIF(current_setting('app.actor_id', true), '')::uuid <> '00000000-0000-0000-0000-000000000000'::uuid);
                CREATE POLICY organization_feature_overrides_update ON platform.organization_feature_overrides FOR UPDATE TO trykatch_platform_runtime
                  USING (current_user = 'trykatch_platform_runtime'
                    AND NULLIF(current_setting('app.actor_id', true), '')::uuid <> '00000000-0000-0000-0000-000000000000'::uuid)
                  WITH CHECK (current_user = 'trykatch_platform_runtime'
                    AND NULLIF(current_setting('app.actor_id', true), '')::uuid <> '00000000-0000-0000-0000-000000000000'::uuid);

                ALTER TABLE platform.feature_control_history ENABLE ROW LEVEL SECURITY;
                ALTER TABLE platform.feature_control_history FORCE ROW LEVEL SECURITY;
                CREATE POLICY feature_control_history_read ON platform.feature_control_history FOR SELECT TO trykatch_platform_runtime
                  USING (current_user = 'trykatch_platform_runtime'
                    AND NULLIF(current_setting('app.actor_id', true), '')::uuid <> '00000000-0000-0000-0000-000000000000'::uuid);
                CREATE POLICY feature_control_history_insert ON platform.feature_control_history FOR INSERT TO trykatch_platform_runtime
                  WITH CHECK (current_user = 'trykatch_platform_runtime'
                    AND NULLIF(current_setting('app.actor_id', true), '')::uuid <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND "ActorId" = NULLIF(current_setting('app.actor_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "feature_control_history",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "feature_environment_controls",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "organization_feature_overrides",
                schema: "platform");
        }
    }
}
