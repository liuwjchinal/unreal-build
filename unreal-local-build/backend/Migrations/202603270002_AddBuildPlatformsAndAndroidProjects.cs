using System;
using Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Migrations;

[DbContext(typeof(BuildDbContext))]
[Migration("202603270002_AddBuildPlatformsAndAndroidProjects")]
public partial class AddBuildPlatformsAndAndroidProjects : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "AndroidEnabled",
            table: "Projects",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<string>(
            name: "AndroidTextureFlavor",
            table: "Projects",
            type: "TEXT",
            maxLength: 50,
            nullable: false,
            defaultValue: "ASTC");

        migrationBuilder.AddColumn<string>(
            name: "Platform",
            table: "Builds",
            type: "TEXT",
            nullable: false,
            defaultValue: "Windows");

        migrationBuilder.AddColumn<string>(
            name: "Platform",
            table: "Schedules",
            type: "TEXT",
            nullable: false,
            defaultValue: "Windows");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "AndroidEnabled", table: "Projects");
        migrationBuilder.DropColumn(name: "AndroidTextureFlavor", table: "Projects");
        migrationBuilder.DropColumn(name: "Platform", table: "Builds");
        migrationBuilder.DropColumn(name: "Platform", table: "Schedules");
    }
}
