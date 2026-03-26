using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Backend.Data;

public static class DatabaseMigrator
{
    public const string InitialMigrationId = "202603260001_InitialSchema";
    private const string ProductVersion = "10.0.5";

    public static async Task MigrateAsync(BuildDbContext dbContext, CancellationToken cancellationToken)
    {
        var connectionString = dbContext.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            await dbContext.Database.MigrateAsync(cancellationToken);
            return;
        }

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var hasProjectsTable = await TableExistsAsync(connection, "Projects", cancellationToken);
        var hasBuildsTable = await TableExistsAsync(connection, "Builds", cancellationToken);
        var hasHistoryTable = await TableExistsAsync(connection, "__EFMigrationsHistory", cancellationToken);

        if ((hasProjectsTable || hasBuildsTable) && !hasHistoryTable)
        {
            await BootstrapLegacyDatabaseAsync(connection, cancellationToken);
        }

        await dbContext.Database.MigrateAsync(cancellationToken);
    }

    private static async Task BootstrapLegacyDatabaseAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(connection, """
            ALTER TABLE Projects ADD COLUMN ProjectKey TEXT NULL;
            """, cancellationToken, ignoreFailure: true);

        await ExecuteNonQueryAsync(connection, """
            ALTER TABLE Projects ADD COLUMN ProjectFingerprint TEXT NULL;
            """, cancellationToken, ignoreFailure: true);

        await ExecuteNonQueryAsync(connection, """
            UPDATE Projects
            SET ProjectKey = COALESCE(NULLIF(ProjectKey, ''), lower(replace(Id, '-', '')))
            WHERE ProjectKey IS NULL OR ProjectKey = '';
            """, cancellationToken);

        await BackfillProjectFingerprintAsync(connection, cancellationToken);

        await ExecuteNonQueryAsync(connection, """
            CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                "ProductVersion" TEXT NOT NULL
            );
            """, cancellationToken);

        await ExecuteNonQueryAsync(connection, """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Projects_ProjectKey" ON "Projects" ("ProjectKey");
            CREATE INDEX IF NOT EXISTS "IX_Projects_ProjectFingerprint" ON "Projects" ("ProjectFingerprint");
            CREATE INDEX IF NOT EXISTS "IX_Builds_FinishedAtUtc" ON "Builds" ("FinishedAtUtc");
            """, cancellationToken);

        await ExecuteNonQueryAsync(connection, $"""
            INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            VALUES ('{InitialMigrationId}', '{ProductVersion}');
            """, cancellationToken);
    }

    private static async Task BackfillProjectFingerprintAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, WorkingCopyPath, UProjectPath, EngineRootPath
            FROM Projects
            WHERE ProjectFingerprint IS NULL OR ProjectFingerprint = '';
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var updates = new List<(string Id, string Fingerprint)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            var workingCopyPath = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var uProjectPath = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            var engineRootPath = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
            updates.Add((id, Services.ProjectIdentity.CreateFingerprint(workingCopyPath, uProjectPath, engineRootPath)));
        }

        foreach (var update in updates)
        {
            var updateCommand = connection.CreateCommand();
            updateCommand.CommandText = """
                UPDATE Projects
                SET ProjectFingerprint = $fingerprint
                WHERE Id = $id;
                """;
            updateCommand.Parameters.AddWithValue("$fingerprint", update.Fingerprint);
            updateCommand.Parameters.AddWithValue("$id", update.Id);
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(1)
            FROM sqlite_master
            WHERE type = 'table' AND name = $tableName;
            """;
        command.Parameters.AddWithValue("$tableName", tableName);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result) > 0;
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        bool ignoreFailure = false)
    {
        try
        {
            var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch when (ignoreFailure)
        {
            // Ignore duplicate-column bootstrap failures on already-patched databases.
        }
    }
}
