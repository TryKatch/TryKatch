using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trykatch.Identity.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIdentityEmailDelivery : Migration
    {
        private static readonly string[] StateLeaseIndex = ["State", "LeaseUntilUtc"];
        private static readonly string[] StateRetryIndex = ["State", "NextAttemptAtUtc"];
        private static readonly string[] UserCreationIndex = ["UserId", "CreatedAtUtc"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "email_deliveries",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TemplateKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    TemplateVersion = table.Column<int>(type: "integer", nullable: false),
                    ProtectedIntent = table.Column<string>(type: "character varying(262144)", maxLength: 262144, nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastCompletedLeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastOutcome = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    LastFailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastSmtpStatusCode = table.Column<int>(type: "integer", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_deliveries", x => x.Id);
                    table.CheckConstraint("CK_identity_email_deliveries_attempts", "\"Attempts\" BETWEEN 0 AND 5");
                    table.CheckConstraint("CK_identity_email_deliveries_lease", "\"State\" <> 'leased' OR (\"LeaseId\" IS NOT NULL AND \"LeaseUntilUtc\" IS NOT NULL)");
                    table.CheckConstraint("CK_identity_email_deliveries_state", "\"State\" IN ('queued', 'leased', 'delivered', 'suppressed', 'terminal')");
                    table.CheckConstraint("CK_identity_email_deliveries_template_version", "\"TemplateVersion\" > 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_email_deliveries_State_LeaseUntilUtc",
                schema: "identity",
                table: "email_deliveries",
                columns: StateLeaseIndex);

            migrationBuilder.CreateIndex(
                name: "IX_email_deliveries_State_NextAttemptAtUtc",
                schema: "identity",
                table: "email_deliveries",
                columns: StateRetryIndex);

            migrationBuilder.CreateIndex(
                name: "IX_email_deliveries_UserId_CreatedAtUtc",
                schema: "identity",
                table: "email_deliveries",
                columns: UserCreationIndex);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "email_deliveries",
                schema: "identity");
        }
    }
}
