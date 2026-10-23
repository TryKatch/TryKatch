using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trykatch.Infrastructure.Persistence.Migrations.Application
{
    /// <inheritdoc />
    public partial class AddModuleExecutionContracts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "infrastructure");

            migrationBuilder.AddColumn<string>(
                name: "ContextJson",
                schema: "platform",
                table: "outbox_messages",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ContextVersion",
                schema: "platform",
                table: "outbox_messages",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayloadHash",
                schema: "platform",
                table: "outbox_messages",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "module_event_deliveries",
                schema: "infrastructure",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConsumerKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    OperationKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    OperationVersion = table.Column<int>(type: "integer", nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    PayloadHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastFailureCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ReplayGeneration = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_module_event_deliveries", x => x.Id);
                    table.CheckConstraint("CK_module_delivery_state", "\"State\" IN ('queued', 'leased', 'paused', 'completed', 'terminal')");
                    table.CheckConstraint("CK_module_delivery_versions", "\"OperationVersion\" > 0 AND \"SchemaVersion\" > 0 AND \"Attempts\" >= 0 AND \"ReplayGeneration\" >= 0");
                });

            migrationBuilder.CreateTable(
                name: "module_event_inbox",
                schema: "app",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConsumerKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProcessedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_module_event_inbox", x => new { x.OrganizationId, x.MessageId, x.ConsumerKey });
                    table.CheckConstraint("CK_module_inbox_version", "\"SchemaVersion\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "module_work_items",
                schema: "infrastructure",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionIdentityKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    JobKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    JobVersion = table.Column<int>(type: "integer", nullable: false),
                    OperationKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    OperationVersion = table.Column<int>(type: "integer", nullable: false),
                    ContextVersion = table.Column<int>(type: "integer", nullable: false),
                    ExecutionContextJson = table.Column<string>(type: "jsonb", nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    PayloadHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IntentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SubmissionMode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DueAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastFailureCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_module_work_items", x => x.Id);
                    table.CheckConstraint("CK_module_work_payload", "octet_length(\"Payload\"::text) <= 1048576");
                    table.CheckConstraint("CK_module_work_state", "\"State\" IN ('queued', 'leased', 'paused', 'completed', 'denied', 'terminal')");
                    table.CheckConstraint("CK_module_work_submission", "(\"SubmissionMode\" = 'enqueue' AND \"DueAtUtc\" IS NULL) OR (\"SubmissionMode\" = 'schedule' AND \"DueAtUtc\" IS NOT NULL)");
                    table.CheckConstraint("CK_module_work_versions", "\"JobVersion\" > 0 AND \"OperationVersion\" > 0 AND \"ContextVersion\" > 0 AND \"Attempts\" >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_module_event_deliveries_MessageId_ConsumerKey",
                schema: "infrastructure",
                table: "module_event_deliveries",
                columns: new[] { "MessageId", "ConsumerKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_module_event_deliveries_State_LeaseUntilUtc_Id",
                schema: "infrastructure",
                table: "module_event_deliveries",
                columns: new[] { "State", "LeaseUntilUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_module_event_inbox_OrganizationId_ProcessedAtUtc",
                schema: "app",
                table: "module_event_inbox",
                columns: new[] { "OrganizationId", "ProcessedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_module_work_items_OrganizationId_ExecutionIdentityKey_JobKe~",
                schema: "infrastructure",
                table: "module_work_items",
                columns: new[] { "OrganizationId", "ExecutionIdentityKey", "JobKey", "JobVersion", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_module_work_items_State_DueAtUtc_LeaseUntilUtc_Id",
                schema: "infrastructure",
                table: "module_work_items",
                columns: new[] { "State", "DueAtUtc", "LeaseUntilUtc", "Id" });
            migrationBuilder.Sql("""
                ALTER TABLE infrastructure.module_work_items ENABLE ROW LEVEL SECURITY;
                ALTER TABLE infrastructure.module_work_items FORCE ROW LEVEL SECURITY;
                CREATE POLICY module_work_items_append ON infrastructure.module_work_items FOR INSERT TO trykatch_org_runtime
                  WITH CHECK ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    AND "ActorId" = NULLIF(current_setting('app.actor_id', true), '')::uuid);
                CREATE POLICY module_work_items_coordinate ON infrastructure.module_work_items FOR ALL TO trykatch_work_coordinator
                  USING (current_user = 'trykatch_work_coordinator') WITH CHECK (current_user = 'trykatch_work_coordinator');
                ALTER TABLE infrastructure.module_event_deliveries ENABLE ROW LEVEL SECURITY;
                ALTER TABLE infrastructure.module_event_deliveries FORCE ROW LEVEL SECURITY;
                CREATE POLICY module_event_deliveries_outbox_read ON infrastructure.module_event_deliveries FOR SELECT TO trykatch_outbox_worker
                  USING (current_user = 'trykatch_outbox_worker');
                CREATE POLICY module_event_deliveries_outbox_insert ON infrastructure.module_event_deliveries FOR INSERT TO trykatch_outbox_worker
                  WITH CHECK (current_user = 'trykatch_outbox_worker');
                CREATE POLICY module_event_deliveries_coordinate ON infrastructure.module_event_deliveries FOR ALL TO trykatch_work_coordinator
                  USING (current_user = 'trykatch_work_coordinator') WITH CHECK (current_user = 'trykatch_work_coordinator');
                ALTER TABLE app.module_event_inbox ENABLE ROW LEVEL SECURITY;
                ALTER TABLE app.module_event_inbox FORCE ROW LEVEL SECURITY;
                CREATE POLICY module_event_inbox_read ON app.module_event_inbox FOR SELECT TO trykatch_org_runtime
                  USING ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid);
                CREATE POLICY module_event_inbox_insert ON app.module_event_inbox FOR INSERT TO trykatch_org_runtime
                  WITH CHECK ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    AND "ActorId" = NULLIF(current_setting('app.actor_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "module_event_deliveries",
                schema: "infrastructure");

            migrationBuilder.DropTable(
                name: "module_event_inbox",
                schema: "app");

            migrationBuilder.DropTable(
                name: "module_work_items",
                schema: "infrastructure");

            migrationBuilder.DropColumn(
                name: "ContextJson",
                schema: "platform",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "ContextVersion",
                schema: "platform",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "PayloadHash",
                schema: "platform",
                table: "outbox_messages");
        }
    }
}
