using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrykatchApp.Infrastructure.Persistence.Migrations.Platform;

[DbContext(typeof(PlatformDbContext))]
[Migration("20260910120000_AddOrganizationDataPlacement")]
public sealed class AddOrganizationDataPlacement : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "organization_data_placements",
            schema: "platform",
            columns: table => new
            {
                OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                Placement = table.Column<int>(type: "integer", nullable: false),
                Provider = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                RegionOrStamp = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                DatabaseIdentifier = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                SecretReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                SchemaVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                State = table.Column<int>(type: "integer", nullable: false),
                FailureCode = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ReadyAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_organization_data_placements", x => x.OrganizationId));

        migrationBuilder.CreateIndex(
            name: "IX_organization_data_placements_State_UpdatedAt",
            schema: "platform",
            table: "organization_data_placements",
            columns: new[] { "State", "UpdatedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "organization_data_placements", schema: "platform");
}
