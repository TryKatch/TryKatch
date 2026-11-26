using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trykatch.Infrastructure.Persistence.Migrations.Application
{
    /// <inheritdoc />
    public partial class AddOrganizationEmailDelivery : Migration
    {
        private static readonly string[] AdmissionIndex = ["OrganizationId", "AdmissionIdentityKey", "TemplateKey", "TemplateVersion", "MessageId"];
        private static readonly string[] OrganizationCreationIndex = ["OrganizationId", "CreatedAtUtc"];
        private static readonly string[] StateLeaseIndex = ["State", "LeaseUntilUtc"];
        private static readonly string[] StateRetryIndex = ["State", "NextAttemptAtUtc"];
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "email_deliveries",
                schema: "infrastructure",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    AdmissionIdentityKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
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
                    table.CheckConstraint("CK_organization_email_deliveries_attempts", "\"Attempts\" BETWEEN 0 AND 5");
                    table.CheckConstraint("CK_organization_email_deliveries_lease", "\"State\" <> 'leased' OR (\"LeaseId\" IS NOT NULL AND \"LeaseUntilUtc\" IS NOT NULL)");
                    table.CheckConstraint("CK_organization_email_deliveries_state", "\"State\" IN ('queued', 'leased', 'delivered', 'suppressed', 'terminal')");
                    table.CheckConstraint("CK_organization_email_deliveries_template_version", "\"TemplateVersion\" > 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_email_deliveries_OrganizationId_AdmissionIdentityKey_Templa~",
                schema: "infrastructure",
                table: "email_deliveries",
                columns: AdmissionIndex,
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_email_deliveries_OrganizationId_CreatedAtUtc",
                schema: "infrastructure",
                table: "email_deliveries",
                columns: OrganizationCreationIndex);

            migrationBuilder.CreateIndex(
                name: "IX_email_deliveries_State_LeaseUntilUtc",
                schema: "infrastructure",
                table: "email_deliveries",
                columns: StateLeaseIndex);

            migrationBuilder.CreateIndex(
                name: "IX_email_deliveries_State_NextAttemptAtUtc",
                schema: "infrastructure",
                table: "email_deliveries",
                columns: StateRetryIndex);

            migrationBuilder.Sql("""
                ALTER TABLE infrastructure.email_deliveries ENABLE ROW LEVEL SECURITY;
                ALTER TABLE infrastructure.email_deliveries FORCE ROW LEVEL SECURITY;
                CREATE POLICY email_deliveries_append ON infrastructure.email_deliveries FOR INSERT TO trykatch_org_runtime
                  WITH CHECK ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    AND "ActorId" = NULLIF(current_setting('app.actor_id', true), '')::uuid);
                CREATE POLICY email_deliveries_coordinate ON infrastructure.email_deliveries FOR ALL TO trykatch_work_coordinator
                  USING (current_user = 'trykatch_work_coordinator') WITH CHECK (current_user = 'trykatch_work_coordinator');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "email_deliveries",
                schema: "infrastructure");
        }
    }
}
