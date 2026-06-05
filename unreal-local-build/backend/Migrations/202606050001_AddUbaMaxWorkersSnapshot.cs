using Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Migrations;

[DbContext(typeof(BuildDbContext))]
[Migration("202606050001_AddUbaMaxWorkersSnapshot")]
public partial class AddUbaMaxWorkersSnapshot : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "UbaMaxWorkers",
            table: "Builds",
            type: "INTEGER",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "UbaMaxWorkers", table: "Builds");
    }
}
