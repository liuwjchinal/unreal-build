using System;
using Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Migrations;

[DbContext(typeof(BuildDbContext))]
[Migration("202603270001_AddBuildSchedules")]
public partial class AddBuildSchedules : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "TriggerSource",
            table: "Builds",
            type: "TEXT",
            nullable: false,
            defaultValue: "Manual");

        migrationBuilder.AddColumn<Guid>(
            name: "ScheduleId",
            table: "Builds",
            type: "TEXT",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "Schedules",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                ScopeType = table.Column<string>(type: "TEXT", nullable: false),
                ProjectId = table.Column<Guid>(type: "TEXT", nullable: true),
                TimeOfDayLocal = table.Column<string>(type: "TEXT", maxLength: 5, nullable: false),
                TargetType = table.Column<string>(type: "TEXT", nullable: false),
                BuildConfiguration = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                Clean = table.Column<bool>(type: "INTEGER", nullable: false),
                Pak = table.Column<bool>(type: "INTEGER", nullable: false),
                IoStore = table.Column<bool>(type: "INTEGER", nullable: false),
                ExtraUatArgs = table.Column<string>(type: "TEXT", nullable: false),
                LastTriggeredAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                LastTriggeredLocalDate = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                LastTriggeredBuildCount = table.Column<int>(type: "INTEGER", nullable: false),
                LastTriggerMessage = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Schedules", x => x.Id);
                table.ForeignKey(
                    name: "FK_Schedules_Projects_ProjectId",
                    column: x => x.ProjectId,
                    principalTable: "Projects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Builds_ScheduleId",
            table: "Builds",
            column: "ScheduleId");

        migrationBuilder.CreateIndex(
            name: "IX_Schedules_Enabled",
            table: "Schedules",
            column: "Enabled");

        migrationBuilder.CreateIndex(
            name: "IX_Schedules_Enabled_TimeOfDayLocal",
            table: "Schedules",
            columns: new[] { "Enabled", "TimeOfDayLocal" });

        migrationBuilder.CreateIndex(
            name: "IX_Schedules_ProjectId",
            table: "Schedules",
            column: "ProjectId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "Schedules");
        migrationBuilder.DropIndex(name: "IX_Builds_ScheduleId", table: "Builds");
        migrationBuilder.DropColumn(name: "TriggerSource", table: "Builds");
        migrationBuilder.DropColumn(name: "ScheduleId", table: "Builds");
    }
}
