using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Trykatch.Infrastructure.Persistence.Migrations.Platform;

[DbContext(typeof(PlatformDbContext))]
[Migration("20261023171629_AllowWorkAuditProvenance")]
public sealed class AllowWorkAuditProvenance : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE platform.audit_intents DROP CONSTRAINT ck_audit_intents_details_allowlist;
        ALTER TABLE platform.audit_intents ADD CONSTRAINT ck_audit_intents_details_allowlist
          CHECK (("Details" - 'expiresAt' - 'permissionCount' - 'reasonProvided' - 'roleCount' - 'status'
            - 'originActorId' - 'executionKind' - 'systemActor') = '{}'::jsonb);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE platform.audit_intents DROP CONSTRAINT ck_audit_intents_details_allowlist;
        -- Preserve append-only historical provenance; enforce the older allowlist on new rows.
        ALTER TABLE platform.audit_intents ADD CONSTRAINT ck_audit_intents_details_allowlist
          CHECK (("Details" - 'expiresAt' - 'permissionCount' - 'reasonProvided' - 'roleCount' - 'status') = '{}'::jsonb) NOT VALID;
        """);
}
