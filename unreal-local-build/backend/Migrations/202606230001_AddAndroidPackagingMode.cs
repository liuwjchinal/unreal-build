using Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Migrations;

[DbContext(typeof(BuildDbContext))]
[Migration("202606230001_AddAndroidPackagingMode")]
public partial class AddAndroidPackagingMode : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "AndroidPackagingMode",
            table: "Builds",
            type: "TEXT",
            maxLength: 50,
            nullable: false,
            defaultValue: "ExternalFilesIoStore");

        migrationBuilder.AddColumn<string>(
            name: "AndroidPackagingMode",
            table: "Schedules",
            type: "TEXT",
            maxLength: 50,
            nullable: false,
            defaultValue: "ExternalFilesIoStore");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "AndroidPackagingMode", table: "Builds");
        migrationBuilder.DropColumn(name: "AndroidPackagingMode", table: "Schedules");
    }
}
