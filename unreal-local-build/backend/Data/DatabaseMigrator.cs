using Microsoft.EntityFrameworkCore;

namespace Backend.Data;

public static class DatabaseMigrator
{
    public const string InitialMigrationId = "202603260001_InitialSchema";

    public static Task MigrateAsync(BuildDbContext dbContext, CancellationToken cancellationToken)
    {
        return dbContext.Database.MigrateAsync(cancellationToken);
    }
}
