using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trykatch.Infrastructure.Persistence.Migrations.Application
{
    /// <inheritdoc />
    public partial class ClassifyOutboxFailures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastErrorCode",
                schema: "platform",
                table: "outbox_messages",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastErrorType",
                schema: "platform",
                table: "outbox_messages",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE platform.outbox_messages
                SET "LastErrorCode" = 'legacy_unclassified',
                    "LastErrorType" = 'legacy_exception',
                    "LastError" = NULL
                WHERE "LastError" IS NOT NULL;

                CREATE OR REPLACE FUNCTION platform.redact_legacy_outbox_error()
                RETURNS trigger
                LANGUAGE plpgsql
                SECURITY INVOKER
                SET search_path = pg_catalog
                AS $function$
                BEGIN
                  IF NEW."LastError" IS NOT NULL THEN
                    NEW."LastErrorCode" := COALESCE(NEW."LastErrorCode", 'legacy_unclassified');
                    NEW."LastErrorType" := COALESCE(NEW."LastErrorType", 'legacy_exception');
                    NEW."LastError" := NULL;
                  END IF;
                  RETURN NEW;
                END
                $function$;

                REVOKE ALL ON FUNCTION platform.redact_legacy_outbox_error() FROM PUBLIC;

                CREATE TRIGGER redact_legacy_outbox_error
                BEFORE INSERT OR UPDATE OF "LastError" ON platform.outbox_messages
                FOR EACH ROW
                EXECUTE FUNCTION platform.redact_legacy_outbox_error();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS redact_legacy_outbox_error ON platform.outbox_messages;
                DROP FUNCTION IF EXISTS platform.redact_legacy_outbox_error();
                """);

            migrationBuilder.DropColumn(
                name: "LastErrorCode",
                schema: "platform",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "LastErrorType",
                schema: "platform",
                table: "outbox_messages");
        }
    }
}
