using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Backend.Data;

public static class SqliteExecution
{
    private const int DefaultTimeoutSeconds = 30;
    private const int MaxSaveRetryCount = 6;

    public static string BuildConnectionString(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = DefaultTimeoutSeconds
        };

        return builder.ToString();
    }

    public static async Task ConfigureDatabaseAsync(BuildDbContext dbContext, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;", cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync($"PRAGMA busy_timeout={DefaultTimeoutSeconds * 1000};", cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;", cancellationToken);
    }

    public static async Task SaveChangesWithRetryAsync(
        DbContext dbContext,
        ILogger logger,
        string operationName,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (Exception ex) when (attempt < MaxSaveRetryCount && IsDatabaseLocked(ex))
            {
                var delay = TimeSpan.FromMilliseconds(150 * attempt);
                logger.LogWarning(
                    ex,
                    "SQLite database is locked during {Operation}. Retrying {Attempt}/{MaxAttempts} after {DelayMs}ms.",
                    operationName,
                    attempt,
                    MaxSaveRetryCount,
                    delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    public static bool IsDatabaseLocked(Exception exception)
    {
        if (exception is SqliteException sqlite && sqlite.SqliteErrorCode == 5)
        {
            return true;
        }

        if (exception is DbUpdateException dbUpdate && dbUpdate.InnerException is not null)
        {
            return IsDatabaseLocked(dbUpdate.InnerException);
        }

        return exception.InnerException is not null && IsDatabaseLocked(exception.InnerException);
    }
}
