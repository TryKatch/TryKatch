using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlatpackApp.Infrastructure.Persistence.Migrations.Platform
{
    /// <inheritdoc />
    public partial class AddRecoverableLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ArchivedAt",
                schema: "platform",
                table: "roles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ArchivedBy",
                schema: "platform",
                table: "roles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                schema: "platform",
                table: "roles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeletedBy",
                schema: "platform",
                table: "roles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletionReason",
                schema: "platform",
                table: "roles",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ArchivedAt",
                schema: "platform",
                table: "memberships",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ArchivedBy",
                schema: "platform",
                table: "memberships",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                schema: "platform",
                table: "memberships",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeletedBy",
                schema: "platform",
                table: "memberships",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletionReason",
                schema: "platform",
                table: "memberships",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ArchivedAt",
                schema: "platform",
                table: "invitations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ArchivedBy",
                schema: "platform",
                table: "invitations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                schema: "platform",
                table: "invitations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeletedBy",
                schema: "platform",
                table: "invitations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletionReason",
                schema: "platform",
                table: "invitations",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                schema: "platform",
                table: "roles");

            migrationBuilder.DropColumn(
                name: "ArchivedBy",
                schema: "platform",
                table: "roles");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "platform",
                table: "roles");

            migrationBuilder.DropColumn(
                name: "DeletedBy",
                schema: "platform",
                table: "roles");

            migrationBuilder.DropColumn(
                name: "DeletionReason",
                schema: "platform",
                table: "roles");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                schema: "platform",
                table: "memberships");

            migrationBuilder.DropColumn(
                name: "ArchivedBy",
                schema: "platform",
                table: "memberships");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "platform",
                table: "memberships");

            migrationBuilder.DropColumn(
                name: "DeletedBy",
                schema: "platform",
                table: "memberships");

            migrationBuilder.DropColumn(
                name: "DeletionReason",
                schema: "platform",
                table: "memberships");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                schema: "platform",
                table: "invitations");

            migrationBuilder.DropColumn(
                name: "ArchivedBy",
                schema: "platform",
                table: "invitations");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "platform",
                table: "invitations");

            migrationBuilder.DropColumn(
                name: "DeletedBy",
                schema: "platform",
                table: "invitations");

            migrationBuilder.DropColumn(
                name: "DeletionReason",
                schema: "platform",
                table: "invitations");
        }
    }
}
