using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrykatchApp.Infrastructure.Persistence.Migrations.Application
{
    /// <inheritdoc />
    public partial class AddRecoverableLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ArchivedBy",
                schema: "app",
                table: "projects",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                schema: "app",
                table: "projects",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeletedBy",
                schema: "app",
                table: "projects",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletionReason",
                schema: "app",
                table: "projects",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ArchivedBy",
                schema: "app",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "app",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "DeletedBy",
                schema: "app",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "DeletionReason",
                schema: "app",
                table: "projects");
        }
    }
}
