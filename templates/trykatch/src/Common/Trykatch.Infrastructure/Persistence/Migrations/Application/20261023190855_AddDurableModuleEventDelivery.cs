using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trykatch.Infrastructure.Persistence.Migrations.Application
{
    /// <inheritdoc />
    public partial class AddDurableModuleEventDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_module_event_deliveries_State_LeaseUntilUtc_Id",
                schema: "infrastructure",
                table: "module_event_deliveries");

            migrationBuilder.AddColumn<string>(
                name: "OperationKey",
                schema: "app",
                table: "module_event_inbox",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "OperationVersion",
                schema: "app",
                table: "module_event_inbox",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PayloadHash",
                schema: "app",
                table: "module_event_inbox",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ConsumerModuleId",
                schema: "infrastructure",
                table: "module_event_deliveries",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAtUtc",
                schema: "infrastructure",
                table: "module_event_deliveries",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<Guid>(
                name: "EffectiveActorId",
                schema: "infrastructure",
                table: "module_event_deliveries",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "EventKey",
                schema: "infrastructure",
                table: "module_event_deliveries",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "GrantKey",
                schema: "infrastructure",
                table: "module_event_deliveries",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "GrantVersion",
                schema: "infrastructure",
                table: "module_event_deliveries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextAttemptAtUtc",
                schema: "infrastructure",
                table: "module_event_deliveries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OriginActorId",
                schema: "infrastructure",
                table: "module_event_deliveries",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_module_event_deliveries_State_NextAttemptAtUtc_LeaseUntilUt~",
                schema: "infrastructure",
                table: "module_event_deliveries",
                columns: new[] { "State", "NextAttemptAtUtc", "LeaseUntilUtc", "CreatedAtUtc", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_module_event_deliveries_State_NextAttemptAtUtc_LeaseUntilUt~",
                schema: "infrastructure",
                table: "module_event_deliveries");

            migrationBuilder.DropColumn(
                name: "OperationKey",
                schema: "app",
                table: "module_event_inbox");

            migrationBuilder.DropColumn(
                name: "OperationVersion",
                schema: "app",
                table: "module_event_inbox");

            migrationBuilder.DropColumn(
                name: "PayloadHash",
                schema: "app",
                table: "module_event_inbox");

            migrationBuilder.DropColumn(
                name: "ConsumerModuleId",
                schema: "infrastructure",
                table: "module_event_deliveries");

            migrationBuilder.DropColumn(
                name: "CreatedAtUtc",
                schema: "infrastructure",
                table: "module_event_deliveries");

            migrationBuilder.DropColumn(
                name: "EffectiveActorId",
                schema: "infrastructure",
                table: "module_event_deliveries");

            migrationBuilder.DropColumn(
                name: "EventKey",
                schema: "infrastructure",
                table: "module_event_deliveries");

            migrationBuilder.DropColumn(
                name: "GrantKey",
                schema: "infrastructure",
                table: "module_event_deliveries");

            migrationBuilder.DropColumn(
                name: "GrantVersion",
                schema: "infrastructure",
                table: "module_event_deliveries");

            migrationBuilder.DropColumn(
                name: "NextAttemptAtUtc",
                schema: "infrastructure",
                table: "module_event_deliveries");

            migrationBuilder.DropColumn(
                name: "OriginActorId",
                schema: "infrastructure",
                table: "module_event_deliveries");

            migrationBuilder.CreateIndex(
                name: "IX_module_event_deliveries_State_LeaseUntilUtc_Id",
                schema: "infrastructure",
                table: "module_event_deliveries",
                columns: new[] { "State", "LeaseUntilUtc", "Id" });
        }
    }
}
