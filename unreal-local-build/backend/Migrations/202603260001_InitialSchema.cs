using System;
using Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Migrations;

[DbContext(typeof(BuildDbContext))]
[Migration(DatabaseMigrator.InitialMigrationId)]
public partial class InitialSchema : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Projects",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ProjectKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                ProjectFingerprint = table.Column<string>(type: "TEXT", maxLength: 3000, nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                WorkingCopyPath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                UProjectPath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                EngineRootPath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                ArchiveRootPath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                GameTarget = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                ClientTarget = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                ServerTarget = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                AllowedBuildConfigurations = table.Column<string>(type: "TEXT", nullable: false),
                DefaultExtraUatArgs = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Projects", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Builds",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                Revision = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                TargetType = table.Column<string>(type: "TEXT", nullable: false),
                TargetName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                BuildConfiguration = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                Clean = table.Column<bool>(type: "INTEGER", nullable: false),
                Pak = table.Column<bool>(type: "INTEGER", nullable: false),
                IoStore = table.Column<bool>(type: "INTEGER", nullable: false),
                ExtraUatArgs = table.Column<string>(type: "TEXT", nullable: false),
                Status = table.Column<string>(type: "TEXT", nullable: false),
                CurrentPhase = table.Column<string>(type: "TEXT", nullable: false),
                ProgressPercent = table.Column<int>(type: "INTEGER", nullable: false),
                StatusMessage = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                QueuedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                StartedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                FinishedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                LogFilePath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                BuildRootPath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                ArchiveDirectoryPath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                ZipFilePath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                DownloadUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                ExitCode = table.Column<int>(type: "INTEGER", nullable: true),
                ErrorSummary = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                LogLineCount = table.Column<long>(type: "INTEGER", nullable: false),
                SvnCommandLine = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                UatCommandLine = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Builds", x => x.Id);
                table.ForeignKey(
                    name: "FK_Builds_Projects_ProjectId",
                    column: x => x.ProjectId,
                    principalTable: "Projects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Builds_FinishedAtUtc",
            table: "Builds",
            column: "FinishedAtUtc");

        migrationBuilder.CreateIndex(
            name: "IX_Builds_ProjectId_QueuedAtUtc",
            table: "Builds",
            columns: new[] { "ProjectId", "QueuedAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_Builds_Status",
            table: "Builds",
            column: "Status");

        migrationBuilder.CreateIndex(
            name: "IX_Projects_ProjectFingerprint",
            table: "Projects",
            column: "ProjectFingerprint");

        migrationBuilder.CreateIndex(
            name: "IX_Projects_ProjectKey",
            table: "Projects",
            column: "ProjectKey",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "Builds");
        migrationBuilder.DropTable(name: "Projects");
    }
}
