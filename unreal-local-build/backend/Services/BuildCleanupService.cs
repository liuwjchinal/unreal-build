using Backend.Data;
using Backend.Models;
using Backend.Options;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

public sealed class BuildCleanupService(
    IDbContextFactory<BuildDbContext> dbFactory,
    AppOptions appOptions,
    ILogger<BuildCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (appOptions.BuildRetentionDays <= 0 || appOptions.CleanupIntervalMinutes <= 0)
        {
            logger.LogInformation("Build cleanup is disabled.");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(appOptions.CleanupIntervalMinutes));

        do
        {
            await CleanupAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        var cutoffUtc = DateTimeOffset.UtcNow.AddDays(-appOptions.BuildRetentionDays);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var expiredBuildCandidates = await db.Builds
            .Where(build => build.FinishedAtUtc != null)
            .ToListAsync(cancellationToken);

        var expiredBuilds = expiredBuildCandidates
            .Where(build =>
                build.FinishedAtUtc < cutoffUtc &&
                build.Status != BuildStatus.Queued &&
                build.Status != BuildStatus.Running &&
                (
                    !string.IsNullOrWhiteSpace(build.DownloadUrl) ||
                    !string.IsNullOrWhiteSpace(build.ZipFilePath) ||
                    !string.IsNullOrWhiteSpace(build.LogFilePath) ||
                    !string.IsNullOrWhiteSpace(build.BuildRootPath) ||
                    !string.IsNullOrWhiteSpace(build.ArchiveDirectoryPath)
                ))
            .ToList();

        if (expiredBuilds.Count == 0)
        {
            return;
        }

        foreach (var build in expiredBuilds)
        {
            DeleteDirectory(build.BuildRootPath);
            DeleteDirectory(build.ArchiveDirectoryPath);

            build.DownloadUrl = null;
            build.ZipFilePath = null;
            build.LogFilePath = string.Empty;
            build.BuildRootPath = string.Empty;
            build.ArchiveDirectoryPath = string.Empty;
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Pruned artifacts and logs for {Count} expired builds.", expiredBuilds.Count);
    }

    private void DeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete expired build directory {Path}", path);
        }
    }
}
