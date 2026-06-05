using Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Migrations;

[DbContext(typeof(BuildDbContext))]
[Migration("202606020001_AddUbaRemoteAgent")]
public partial class AddUbaRemoteAgent : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "DefaultBuildAccelerator",
            table: "Projects",
            type: "TEXT",
            maxLength: 50,
            nullable: false,
            defaultValue: "None");

        migrationBuilder.AddColumn<string>(
            name: "BuildAccelerator",
            table: "Builds",
            type: "TEXT",
            maxLength: 50,
            nullable: false,
            defaultValue: "None");

        migrationBuilder.AddColumn<bool>(
            name: "UbaRemoteEnabled",
            table: "Builds",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "UbaHost",
            table: "Builds",
            type: "TEXT",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "UbaPort",
            table: "Builds",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "UbaAgentJoinUrl",
            table: "Builds",
            type: "TEXT",
            maxLength: 1000,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BuildAccelerator",
            table: "Schedules",
            type: "TEXT",
            maxLength: 50,
            nullable: false,
            defaultValue: "None");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "DefaultBuildAccelerator", table: "Projects");
        migrationBuilder.DropColumn(name: "BuildAccelerator", table: "Builds");
        migrationBuilder.DropColumn(name: "UbaRemoteEnabled", table: "Builds");
        migrationBuilder.DropColumn(name: "UbaHost", table: "Builds");
        migrationBuilder.DropColumn(name: "UbaPort", table: "Builds");
        migrationBuilder.DropColumn(name: "UbaAgentJoinUrl", table: "Builds");
        migrationBuilder.DropColumn(name: "BuildAccelerator", table: "Schedules");
    }
}
