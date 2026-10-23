using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trykatch.Infrastructure.Persistence.Migrations.Application
{
    /// <inheritdoc />
    public partial class AddDurableModuleJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OccurrenceAtUtc",
                schema: "infrastructure",
                table: "module_work_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OperatorPaused",
                schema: "infrastructure",
                table: "module_work_items",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RecoveryGeneration",
                schema: "infrastructure",
                table: "module_work_items",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ScheduleKey",
                schema: "infrastructure",
                table: "module_work_items",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScheduleTargetKey",
                schema: "infrastructure",
                table: "module_work_items",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "module_schedule_cursors",
                schema: "infrastructure",
                columns: table => new
                {
                    ScheduleKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    TargetKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConfigurationHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StartAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReconciledThroughUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SkippedOccurrences = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_module_schedule_cursors", x => new { x.ScheduleKey, x.TargetKey, x.OrganizationId });
                    table.CheckConstraint("CK_module_schedule_skipped", "\"SkippedOccurrences\" >= 0");
                });

            migrationBuilder.CreateTable(
                name: "module_work_controls",
                schema: "infrastructure",
                columns: table => new
                {
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ExpectedGeneration = table.Column<int>(type: "integer", nullable: false),
                    ResultGeneration = table.Column<int>(type: "integer", nullable: false),
                    OperatorPaused = table.Column<bool>(type: "boolean", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_module_work_controls", x => x.RequestId);
                    table.CheckConstraint("CK_module_control_action", "\"Action\" IN ('pause','resume','retry')");
                    table.CheckConstraint("CK_module_control_generation", "\"ExpectedGeneration\" >= 0 AND \"ResultGeneration\" > \"ExpectedGeneration\"");
                });

            migrationBuilder.CreateIndex(
                name: "IX_module_work_items_OrganizationId_ScheduleKey_ScheduleTarget~",
                schema: "infrastructure",
                table: "module_work_items",
                columns: new[] { "OrganizationId", "ScheduleKey", "ScheduleTargetKey", "OccurrenceAtUtc" },
                unique: true,
                filter: "\"ScheduleKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_module_work_controls_WorkId_OccurredAtUtc",
                schema: "infrastructure",
                table: "module_work_controls",
                columns: new[] { "WorkId", "OccurredAtUtc" });
            migrationBuilder.Sql("""
                -- Quartz.NET v4.6.0 PostgreSQL base DDL, Apache-2.0.
                -- Source: https://github.com/quartznet/quartznet/blob/v4.6.0/database/tables/tables_postgres.sql
                -- Original SHA256: 37c62b60adebd683fe96e13e397a2fe40e983317a15d37fba1c5ce3da754d27b
                -- Adaptations: infrastructure schema; no destructive reset or optional history; exact coordinator RLS.
                CREATE TABLE infrastructure.qrtz_job_details
                  (
                    sched_name TEXT NOT NULL,
                    job_name TEXT NOT NULL,
                    job_group TEXT NOT NULL,
                    description TEXT NULL,
                    job_class_name TEXT NOT NULL,
                    is_durable BOOL NOT NULL,
                    is_nonconcurrent BOOL NOT NULL,
                    is_update_data BOOL NOT NULL,
                    requests_recovery BOOL NOT NULL,
                    job_data BYTEA NULL,
                    PRIMARY KEY (sched_name, job_name, job_group)
                );

                CREATE TABLE infrastructure.qrtz_triggers
                  (
                    sched_name TEXT NOT NULL,
                    trigger_name TEXT NOT NULL,
                    trigger_group TEXT NOT NULL,
                    job_name TEXT NOT NULL,
                    job_group TEXT NOT NULL,
                    description TEXT NULL,
                    next_fire_time BIGINT NULL,
                    prev_fire_time BIGINT NULL,
                    priority INTEGER NULL,
                    trigger_state TEXT NOT NULL,
                    trigger_type TEXT NOT NULL,
                    start_time BIGINT NOT NULL,
                    end_time BIGINT NULL,
                    calendar_name TEXT NULL,
                    misfire_instr SMALLINT NULL,
                    misfire_orig_fire_time BIGINT NULL,
                    execution_group VARCHAR(200) NULL,
                    preferred_node VARCHAR(200) NULL,
                    preferred_node_auto BOOL NOT NULL DEFAULT FALSE,
                    retry_policy VARCHAR(250) NULL,
                    retry_attempt INTEGER NULL,
                    continues_trigger_name TEXT NULL,
                    continues_trigger_group TEXT NULL,
                    continuation_condition INTEGER NULL,
                    overlap_policy INTEGER NULL,
                    pause_reason VARCHAR(250) NULL,
                    paused_by VARCHAR(200) NULL,
                    paused_at BIGINT NULL,
                    misfire_threshold BIGINT NULL,
                    job_data BYTEA NULL,
                    PRIMARY KEY (sched_name, trigger_name, trigger_group),
                    FOREIGN KEY (sched_name, job_name, job_group)
                      REFERENCES infrastructure.qrtz_job_details (sched_name, job_name, job_group)
                );

                CREATE TABLE infrastructure.qrtz_simple_triggers
                  (
                    sched_name TEXT NOT NULL,
                    trigger_name TEXT NOT NULL,
                    trigger_group TEXT NOT NULL,
                    repeat_count BIGINT NOT NULL,
                    repeat_interval BIGINT NOT NULL,
                    times_triggered BIGINT NOT NULL,
                    PRIMARY KEY (sched_name, trigger_name, trigger_group),
                    FOREIGN KEY (sched_name, trigger_name, trigger_group)
                      REFERENCES infrastructure.qrtz_triggers (sched_name, trigger_name, trigger_group)
                      ON DELETE CASCADE
                );

                CREATE TABLE infrastructure.qrtz_simprop_triggers
                  (
                    sched_name TEXT NOT NULL,
                    trigger_name TEXT NOT NULL,
                    trigger_group TEXT NOT NULL,
                    str_prop_1 TEXT NULL,
                    str_prop_2 TEXT NULL,
                    str_prop_3 TEXT NULL,
                    int_prop_1 INTEGER NULL,
                    int_prop_2 INTEGER NULL,
                    long_prop_1 BIGINT NULL,
                    long_prop_2 BIGINT NULL,
                    dec_prop_1 NUMERIC NULL,
                    dec_prop_2 NUMERIC NULL,
                    bool_prop_1 BOOL NULL,
                    bool_prop_2 BOOL NULL,
                    time_zone_id TEXT NULL,
                    PRIMARY KEY (sched_name, trigger_name, trigger_group),
                    FOREIGN KEY (sched_name, trigger_name, trigger_group)
                      REFERENCES infrastructure.qrtz_triggers (sched_name, trigger_name, trigger_group)
                      ON DELETE CASCADE
                );

                CREATE TABLE infrastructure.qrtz_cron_triggers
                  (
                    sched_name TEXT NOT NULL,
                    trigger_name TEXT NOT NULL,
                    trigger_group TEXT NOT NULL,
                    cron_expression TEXT NOT NULL,
                    time_zone_id TEXT,
                    PRIMARY KEY (sched_name, trigger_name, trigger_group),
                    FOREIGN KEY (sched_name, trigger_name, trigger_group)
                      REFERENCES infrastructure.qrtz_triggers (sched_name, trigger_name, trigger_group)
                      ON DELETE CASCADE
                );

                CREATE TABLE infrastructure.qrtz_blob_triggers
                  (
                    sched_name TEXT NOT NULL,
                    trigger_name TEXT NOT NULL,
                    trigger_group TEXT NOT NULL,
                    blob_data BYTEA NULL,
                    PRIMARY KEY (sched_name, trigger_name, trigger_group),
                    FOREIGN KEY (sched_name, trigger_name, trigger_group)
                      REFERENCES infrastructure.qrtz_triggers (sched_name, trigger_name, trigger_group)
                      ON DELETE CASCADE
                );

                CREATE TABLE infrastructure.qrtz_calendars
                  (
                    sched_name TEXT NOT NULL,
                    calendar_name TEXT NOT NULL,
                    calendar BYTEA NOT NULL,
                    PRIMARY KEY (sched_name, calendar_name)
                );

                CREATE TABLE infrastructure.qrtz_paused_trigger_grps
                  (
                    sched_name TEXT NOT NULL,
                    trigger_group TEXT NOT NULL,
                    pause_reason VARCHAR(250) NULL,
                    paused_by VARCHAR(200) NULL,
                    paused_at BIGINT NULL,
                    PRIMARY KEY (sched_name, trigger_group)
                );

                CREATE TABLE infrastructure.qrtz_paused_job_grps
                  (
                    sched_name TEXT NOT NULL,
                    job_group TEXT NOT NULL,
                    pause_reason VARCHAR(250) NULL,
                    paused_by VARCHAR(200) NULL,
                    paused_at BIGINT NULL,
                    PRIMARY KEY (sched_name, job_group)
                );

                CREATE TABLE infrastructure.qrtz_fired_triggers
                  (
                    sched_name TEXT NOT NULL,
                    entry_id TEXT NOT NULL,
                    trigger_name TEXT NOT NULL,
                    trigger_group TEXT NOT NULL,
                    instance_name TEXT NOT NULL,
                    fired_time BIGINT NOT NULL,
                    sched_time BIGINT NOT NULL,
                    priority INTEGER NOT NULL,
                    state TEXT NOT NULL,
                    job_name TEXT NULL,
                    job_group TEXT NULL,
                    is_nonconcurrent BOOL NOT NULL,
                    requests_recovery BOOL NULL,
                    execution_group VARCHAR(200) NULL,
                    progress INTEGER NULL,
                    progress_message VARCHAR(250) NULL,
                    interrupt_requested_time BIGINT NULL,
                    PRIMARY KEY (sched_name, entry_id)
                );

                CREATE TABLE infrastructure.qrtz_scheduler_state
                  (
                    sched_name TEXT NOT NULL,
                    instance_name TEXT NOT NULL,
                    last_checkin_time BIGINT NOT NULL,
                    checkin_interval BIGINT NOT NULL,
                    quartz_version VARCHAR(20) NULL,
                    PRIMARY KEY (sched_name, instance_name)
                );

                CREATE TABLE infrastructure.qrtz_locks
                  (
                    sched_name TEXT NOT NULL,
                    lock_name TEXT NOT NULL,
                    PRIMARY KEY (sched_name, lock_name)
                );

                CREATE INDEX idx_qrtz_j_g_n ON infrastructure.qrtz_job_details (sched_name, job_group, job_name);

                CREATE INDEX idx_qrtz_t_j ON infrastructure.qrtz_triggers (sched_name, job_name, job_group);

                CREATE INDEX idx_qrtz_t_c ON infrastructure.qrtz_triggers (sched_name, calendar_name);

                CREATE INDEX idx_qrtz_t_g_n ON infrastructure.qrtz_triggers (sched_name, trigger_group, trigger_name);

                CREATE INDEX idx_qrtz_t_nft_st ON infrastructure.qrtz_triggers (sched_name, trigger_state, next_fire_time asc, priority desc, misfire_instr);

                CREATE INDEX idx_qrtz_ft_inst_job_req_rcvry ON infrastructure.qrtz_fired_triggers (sched_name, instance_name, requests_recovery);

                CREATE INDEX idx_qrtz_ft_j_g ON infrastructure.qrtz_fired_triggers (sched_name, job_name, job_group);

                CREATE INDEX idx_qrtz_ft_t_g ON infrastructure.qrtz_fired_triggers (sched_name, trigger_name, trigger_group);
                ALTER TABLE infrastructure.qrtz_job_details ENABLE ROW LEVEL SECURITY;
                ALTER TABLE infrastructure.qrtz_job_details FORCE ROW LEVEL SECURITY;
                CREATE POLICY qrtz_job_details_coordinate ON infrastructure.qrtz_job_details
                    FOR ALL TO trykatch_work_coordinator
                    USING (CURRENT_USER = 'trykatch_work_coordinator'::name)
                    WITH CHECK (CURRENT_USER = 'trykatch_work_coordinator'::name);

                ALTER TABLE infrastructure.qrtz_triggers ENABLE ROW LEVEL SECURITY;
                ALTER TABLE infrastructure.qrtz_triggers FORCE ROW LEVEL SECURITY;
                CREATE POLICY qrtz_triggers_coordinate ON infrastructure.qrtz_triggers
                    FOR ALL TO trykatch_work_coordinator
                    USING (CURRENT_USER = 'trykatch_work_coordinator'::name)
                    WITH CHECK (CURRENT_USER = 'trykatch_work_coordinator'::name);

                ALTER TABLE infrastructure.qrtz_simple_triggers ENABLE ROW LEVEL SECURITY;
                ALTER TABLE infrastructure.qrtz_simple_triggers FORCE ROW LEVEL SECURITY;
                CREATE POLICY qrtz_simple_triggers_coordinate ON infrastructure.qrtz_simple_triggers
                    FOR ALL TO trykatch_work_coordinator
                    USING (CURRENT_USER = 'trykatch_work_coordinator'::name)
                    WITH CHECK (CURRENT_USER = 'trykatch_work_coordinator'::name);

                ALTER TABLE infrastructure.qrtz_simprop_triggers ENABLE ROW LEVEL SECURITY;
                ALTER TABLE infrastructure.qrtz_simprop_triggers FORCE ROW LEVEL SECURITY;
                CREATE POLICY qrtz_simprop_triggers_coordinate ON infrastructure.qrtz_simprop_triggers
                    FOR ALL TO trykatch_work_coordinator
                    USING (CURRENT_USER = 'trykatch_work_coordinator'::name)
                    WITH CHECK (CURRENT_USER = 'trykatch_work_coordinator'::name);

                ALTER TABLE infrastructure.qrtz_cron_triggers ENABLE ROW LEVEL SECURITY;
                ALTER TABLE infrastructure.qrtz_cron_triggers FORCE ROW LEVEL SECURITY;
                CREATE POLICY qrtz_cron_triggers_coordinate ON infrastructure.qrtz_cron_triggers
                    FOR ALL TO trykatch_work_coordinator
                    USING (CURRENT_USER = 'trykatch_work_coordinator'::name)
                    WITH CHECK (CURRENT_USER = 'trykatch_work_coordinator'::name);

                ALTER TABLE infrastructure.qrtz_blob_triggers ENABLE ROW LEVEL SECURITY;
                ALTER TABLE infrastructure.qrtz_blob_triggers FORCE ROW LEVEL SECURITY;
                CREATE POLICY qrtz_blob_triggers_coordinate ON infrastructure.qrtz_blob_triggers
                    FOR ALL TO trykatch_work_coordinator
                    USING (CURRENT_USER = 'trykatch_work_coordinator'::name)
                    WITH CHECK (CURRENT_USER = 'trykatch_work_coordinator'::name);

                ALTER TABLE infrastructure.qrtz_calendars ENABLE ROW LEVEL SECURITY;
                ALTER TABLE infrastructure.qrtz_calendars FORCE ROW LEVEL SECURITY;
                CREATE POLICY qrtz_calendars_coordinate ON infrastructure.qrtz_calendars
                    FOR ALL TO trykatch_work_coordinator
                    USING (CURRENT_USER = 'trykatch_work_coordinator'::name)
                    WITH CHECK (CURRENT_USER = 'trykatch_work_coordinator'::name);

                ALTER TABLE infrastructure.qrtz_paused_trigger_grps ENABLE ROW LEVEL SECURITY;
                ALTER TABLE infrastructure.qrtz_paused_trigger_grps FORCE ROW LEVEL SECURITY;
                CREATE POLICY qrtz_paused_trigger_grps_coordinate ON infrastructure.qrtz_paused_trigger_grps
                    FOR ALL TO trykatch_work_coordinator
                    USING (CURRENT_USER = 'trykatch_work_coordinator'::name)
                    WITH CHECK (CURRENT_USER = 'trykatch_work_coordinator'::name);

                ALTER TABLE infrastructure.qrtz_paused_job_grps ENABLE ROW LEVEL SECURITY;
                ALTER TABLE infrastructure.qrtz_paused_job_grps FORCE ROW LEVEL SECURITY;
                CREATE POLICY qrtz_paused_job_grps_coordinate ON infrastructure.qrtz_paused_job_grps
                    FOR ALL TO trykatch_work_coordinator
                    USING (CURRENT_USER = 'trykatch_work_coordinator'::name)
                    WITH CHECK (CURRENT_USER = 'trykatch_work_coordinator'::name);

                ALTER TABLE infrastructure.qrtz_fired_triggers ENABLE ROW LEVEL SECURITY;
                ALTER TABLE infrastructure.qrtz_fired_triggers FORCE ROW LEVEL SECURITY;
                CREATE POLICY qrtz_fired_triggers_coordinate ON infrastructure.qrtz_fired_triggers
                    FOR ALL TO trykatch_work_coordinator
                    USING (CURRENT_USER = 'trykatch_work_coordinator'::name)
                    WITH CHECK (CURRENT_USER = 'trykatch_work_coordinator'::name);

                ALTER TABLE infrastructure.qrtz_scheduler_state ENABLE ROW LEVEL SECURITY;
                ALTER TABLE infrastructure.qrtz_scheduler_state FORCE ROW LEVEL SECURITY;
                CREATE POLICY qrtz_scheduler_state_coordinate ON infrastructure.qrtz_scheduler_state
                    FOR ALL TO trykatch_work_coordinator
                    USING (CURRENT_USER = 'trykatch_work_coordinator'::name)
                    WITH CHECK (CURRENT_USER = 'trykatch_work_coordinator'::name);

                ALTER TABLE infrastructure.qrtz_locks ENABLE ROW LEVEL SECURITY;
                ALTER TABLE infrastructure.qrtz_locks FORCE ROW LEVEL SECURITY;
                CREATE POLICY qrtz_locks_coordinate ON infrastructure.qrtz_locks
                    FOR ALL TO trykatch_work_coordinator
                    USING (CURRENT_USER = 'trykatch_work_coordinator'::name)
                    WITH CHECK (CURRENT_USER = 'trykatch_work_coordinator'::name);

                ALTER TABLE infrastructure.module_schedule_cursors ENABLE ROW LEVEL SECURITY;
                ALTER TABLE infrastructure.module_schedule_cursors FORCE ROW LEVEL SECURITY;
                CREATE POLICY module_schedule_cursors_coordinate ON infrastructure.module_schedule_cursors
                    FOR ALL TO trykatch_work_coordinator
                    USING (CURRENT_USER = 'trykatch_work_coordinator'::name)
                    WITH CHECK (CURRENT_USER = 'trykatch_work_coordinator'::name);

                ALTER TABLE infrastructure.module_work_controls ENABLE ROW LEVEL SECURITY;
                ALTER TABLE infrastructure.module_work_controls FORCE ROW LEVEL SECURITY;
                CREATE POLICY module_work_controls_coordinate ON infrastructure.module_work_controls
                    FOR ALL TO trykatch_work_coordinator
                    USING (CURRENT_USER = 'trykatch_work_coordinator'::name)
                    WITH CHECK (CURRENT_USER = 'trykatch_work_coordinator'::name);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TABLE infrastructure.qrtz_locks;
                DROP TABLE infrastructure.qrtz_scheduler_state;
                DROP TABLE infrastructure.qrtz_fired_triggers;
                DROP TABLE infrastructure.qrtz_paused_job_grps;
                DROP TABLE infrastructure.qrtz_paused_trigger_grps;
                DROP TABLE infrastructure.qrtz_calendars;
                DROP TABLE infrastructure.qrtz_blob_triggers;
                DROP TABLE infrastructure.qrtz_cron_triggers;
                DROP TABLE infrastructure.qrtz_simprop_triggers;
                DROP TABLE infrastructure.qrtz_simple_triggers;
                DROP TABLE infrastructure.qrtz_triggers;
                DROP TABLE infrastructure.qrtz_job_details;
                """);
            migrationBuilder.DropTable(
                name: "module_schedule_cursors",
                schema: "infrastructure");

            migrationBuilder.DropTable(
                name: "module_work_controls",
                schema: "infrastructure");

            migrationBuilder.DropIndex(
                name: "IX_module_work_items_OrganizationId_ScheduleKey_ScheduleTarget~",
                schema: "infrastructure",
                table: "module_work_items");

            migrationBuilder.DropColumn(
                name: "OccurrenceAtUtc",
                schema: "infrastructure",
                table: "module_work_items");

            migrationBuilder.DropColumn(
                name: "OperatorPaused",
                schema: "infrastructure",
                table: "module_work_items");

            migrationBuilder.DropColumn(
                name: "RecoveryGeneration",
                schema: "infrastructure",
                table: "module_work_items");

            migrationBuilder.DropColumn(
                name: "ScheduleKey",
                schema: "infrastructure",
                table: "module_work_items");

            migrationBuilder.DropColumn(
                name: "ScheduleTargetKey",
                schema: "infrastructure",
                table: "module_work_items");
        }
    }
}
