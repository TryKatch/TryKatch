using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trykatch.Infrastructure.Persistence.Migrations.Application;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260911173000_AllowAuditProjectionRead")]
public sealed class AllowAuditProjectionRead : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE POLICY audit_projection_worker_read ON platform.audit_entries FOR SELECT TO trykatch_outbox_worker
          USING (current_user = 'trykatch_outbox_worker');
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP POLICY IF EXISTS audit_projection_worker_read ON platform.audit_entries;
        """);
}
