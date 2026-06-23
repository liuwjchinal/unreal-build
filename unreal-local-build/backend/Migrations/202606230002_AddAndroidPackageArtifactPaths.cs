using Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Migrations;

[DbContext(typeof(BuildDbContext))]
[Migration("202606230002_AddAndroidPackageArtifactPaths")]
public partial class AddAndroidPackageArtifactPaths : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "AndroidPackageManifestPath",
            table: "Builds",
            type: "TEXT",
            maxLength: 1000,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "AndroidInstallScriptPath",
            table: "Builds",
            type: "TEXT",
            maxLength: 1000,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "AndroidPackageManifestPath", table: "Builds");
        migrationBuilder.DropColumn(name: "AndroidInstallScriptPath", table: "Builds");
    }
}
