using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace Trykatch.Infrastructure.Persistence.Migrations.Application;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260911210000_AddOutboxRecovery")]
public partial class AddOutboxRecovery : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "ExhaustedAt", schema: "platform", table: "outbox_messages",
            type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<int>(
            name: "ReplayGeneration", schema: "platform", table: "outbox_messages",
            type: "integer", nullable: false, defaultValue: 0);

        migrationBuilder.CreateTable(
            name: "outbox_replay_requests", schema: "platform",
            columns: table => new
            {
                RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                ExpectedFailedGeneration = table.Column<int>(type: "integer", nullable: false),
                ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_outbox_replay_requests", x => x.RequestId);
                table.CheckConstraint("CK_outbox_replay_requests_generation", "\"ExpectedFailedGeneration\" >= 0");
            });

        migrationBuilder.CreateTable(
            name: "outbox_recovery_events", schema: "platform",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                ReplayGeneration = table.Column<int>(type: "integer", nullable: false),
                Outcome = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                FailureCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                FailureType = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: true),
                OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                RequestId = table.Column<Guid>(type: "uuid", nullable: true),
                ActorId = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_outbox_recovery_events", x => x.Id);
                table.CheckConstraint("CK_outbox_recovery_events_generation", "\"ReplayGeneration\" >= 0");
                table.CheckConstraint("CK_outbox_recovery_events_outcome", "\"Outcome\" IN ('terminal', 'replayed', 'not_found', 'not_exhausted', 'stale_generation')");
                table.CheckConstraint("CK_outbox_recovery_events_terminal", "(\"Outcome\" = 'terminal' AND \"RequestId\" IS NULL AND \"ActorId\" IS NULL AND \"FailureCode\" IS NOT NULL AND \"FailureType\" IS NOT NULL) OR (\"Outcome\" <> 'terminal' AND \"RequestId\" IS NOT NULL AND \"ActorId\" IS NOT NULL AND \"FailureCode\" IS NULL AND \"FailureType\" IS NULL)");
            });

        migrationBuilder.DropIndex(
            name: "IX_outbox_messages_ProcessedAt_OccurredAt", schema: "platform",
            table: "outbox_messages");
        migrationBuilder.CreateIndex(
            name: "IX_outbox_messages_ProcessedAt_ExhaustedAt_OccurredAt_Id", schema: "platform",
            table: "outbox_messages", columns: new[] { "ProcessedAt", "ExhaustedAt", "OccurredAt", "Id" });
        migrationBuilder.CreateIndex(
            name: "IX_outbox_replay_requests_RequestedAt_RequestId", schema: "platform",
            table: "outbox_replay_requests", columns: new[] { "RequestedAt", "RequestId" });
        migrationBuilder.CreateIndex(
            name: "IX_outbox_recovery_events_OccurredAt_MessageId_ReplayGeneration", schema: "platform",
            table: "outbox_recovery_events", columns: new[] { "OccurredAt", "MessageId", "ReplayGeneration" });
        migrationBuilder.CreateIndex(
            name: "IX_outbox_recovery_events_RequestId", schema: "platform",
            table: "outbox_recovery_events", column: "RequestId", unique: true,
            filter: "\"RequestId\" IS NOT NULL");
        migrationBuilder.CreateIndex(
            name: "IX_outbox_recovery_events_MessageId_ReplayGeneration_Outcome", schema: "platform",
            table: "outbox_recovery_events", columns: new[] { "MessageId", "ReplayGeneration", "Outcome" },
            unique: true, filter: "\"Outcome\" = 'terminal'");

        migrationBuilder.Sql("""
            ALTER TABLE platform.outbox_replay_requests ENABLE ROW LEVEL SECURITY;
            ALTER TABLE platform.outbox_replay_requests FORCE ROW LEVEL SECURITY;
            CREATE POLICY outbox_replay_requests_platform_read ON platform.outbox_replay_requests
              FOR SELECT TO trykatch_platform_runtime
              USING (current_user = 'trykatch_platform_runtime');
            CREATE POLICY outbox_replay_requests_platform_insert ON platform.outbox_replay_requests
              FOR INSERT TO trykatch_platform_runtime
              WITH CHECK (current_user = 'trykatch_platform_runtime'
                AND "ActorId" = NULLIF(current_setting('app.actor_id', true), '')::uuid
                AND "RequestedAt" = CURRENT_TIMESTAMP);
            CREATE POLICY outbox_replay_requests_worker_read ON platform.outbox_replay_requests
              FOR SELECT TO trykatch_outbox_worker
              USING (current_user = 'trykatch_outbox_worker');

            ALTER TABLE platform.outbox_recovery_events ENABLE ROW LEVEL SECURITY;
            ALTER TABLE platform.outbox_recovery_events FORCE ROW LEVEL SECURITY;
            CREATE POLICY outbox_recovery_events_platform_read ON platform.outbox_recovery_events
              FOR SELECT TO trykatch_platform_runtime
              USING (current_user = 'trykatch_platform_runtime');
            CREATE POLICY outbox_recovery_events_worker_read ON platform.outbox_recovery_events
              FOR SELECT TO trykatch_outbox_worker
              USING (current_user = 'trykatch_outbox_worker');
            CREATE POLICY outbox_recovery_events_worker_insert ON platform.outbox_recovery_events
              FOR INSERT TO trykatch_outbox_worker
              WITH CHECK (current_user = 'trykatch_outbox_worker');

            UPDATE platform.outbox_messages
            SET "ExhaustedAt" = CURRENT_TIMESTAMP,
                "LastErrorCode" = COALESCE("LastErrorCode", 'attempts_exhausted'),
                "LastErrorType" = COALESCE("LastErrorType", 'transport_failure')
            WHERE "ProcessedAt" IS NULL AND "Attempts" >= 10;

            INSERT INTO platform.outbox_recovery_events
              ("Id", "MessageId", "ReplayGeneration", "Outcome", "FailureCode", "FailureType", "OccurredAt")
            SELECT gen_random_uuid(), "Id", "ReplayGeneration", 'terminal', "LastErrorCode", left("LastErrorType", 240), "ExhaustedAt"
            FROM platform.outbox_messages
            WHERE "ProcessedAt" IS NULL AND "ExhaustedAt" IS NOT NULL
            ON CONFLICT DO NOTHING;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Outbox recovery history cannot be removed by downgrade.");
}
