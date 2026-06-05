using Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Migrations;

[DbContext(typeof(BuildDbContext))]
[Migration("202606020002_AddUbaRemoteAgentSnapshots")]
public partial class AddUbaRemoteAgentSnapshots : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "UbaListenHost",
            table: "Builds",
            type: "TEXT",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "UbaAgentMaxIdleSeconds",
            table: "Builds",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "UbaAgentStoreCapacityGb",
            table: "Builds",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "UbaAgentManualCommand",
            table: "Builds",
            type: "TEXT",
            maxLength: 1000,
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "UbaHostAutoDetected",
            table: "Builds",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "UbaHostWarning",
            table: "Builds",
            type: "TEXT",
            maxLength: 1000,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "UbaListenHost", table: "Builds");
        migrationBuilder.DropColumn(name: "UbaAgentMaxIdleSeconds", table: "Builds");
        migrationBuilder.DropColumn(name: "UbaAgentStoreCapacityGb", table: "Builds");
        migrationBuilder.DropColumn(name: "UbaAgentManualCommand", table: "Builds");
        migrationBuilder.DropColumn(name: "UbaHostAutoDetected", table: "Builds");
        migrationBuilder.DropColumn(name: "UbaHostWarning", table: "Builds");
    }
}
