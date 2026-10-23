using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trykatch.Infrastructure.Persistence.Migrations.Application
{
    /// <inheritdoc />
    public partial class AddOrganizationWorkExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextAttemptAtUtc",
                schema: "infrastructure",
                table: "module_work_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "module_work_completions",
                schema: "app",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    OperationVersion = table.Column<int>(type: "integer", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    PayloadHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_module_work_completions", x => new { x.OrganizationId, x.WorkId });
                    table.CheckConstraint("CK_module_completion_version", "\"OperationVersion\" > 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_module_work_completions_OrganizationId_CompletedAtUtc",
                schema: "app",
                table: "module_work_completions",
                columns: new[] { "OrganizationId", "CompletedAtUtc" });

            // Historical policy text is immutable; runtime contracts are inspected separately.
            migrationBuilder.Sql("""
                ALTER TABLE app.module_work_completions ENABLE ROW LEVEL SECURITY;
                ALTER TABLE app.module_work_completions FORCE ROW LEVEL SECURITY;
                CREATE POLICY module_work_completions_read ON app.module_work_completions FOR SELECT TO trykatch_org_runtime
                  USING ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid);
                CREATE POLICY module_work_completions_insert ON app.module_work_completions FOR INSERT TO trykatch_org_runtime
                  WITH CHECK ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    AND "ActorId" = NULLIF(current_setting('app.actor_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "module_work_completions",
                schema: "app");

            migrationBuilder.DropColumn(
                name: "NextAttemptAtUtc",
                schema: "infrastructure",
                table: "module_work_items");
        }
    }
}
