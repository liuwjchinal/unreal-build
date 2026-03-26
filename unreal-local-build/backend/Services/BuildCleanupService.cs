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
        if (appOptions.CleanupIntervalMinutes <= 0 || !HasAnyCleanupRuleEnabled())
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
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var finishedBuilds = await db.Builds
            .Where(build => build.FinishedAtUtc != null)
            .ToListAsync(cancellationToken);

        var prunableBuilds = finishedBuilds
            .Where(build =>
                build.Status != BuildStatus.Queued &&
                build.Status != BuildStatus.Running &&
                HasPrunableFiles(build))
            .ToList();

        if (prunableBuilds.Count == 0)
        {
            return;
        }

        var buildsToPrune = new Dictionary<Guid, BuildRecord>();
        var retentionCutoffUtc = DateTimeOffset.UtcNow.AddDays(-appOptions.BuildRetentionDays);

        if (appOptions.BuildRetentionDays > 0)
        {
            foreach (var build in prunableBuilds.Where(build => build.FinishedAtUtc < retentionCutoffUtc))
            {
                buildsToPrune[build.Id] = build;
            }
        }

        if (appOptions.KeepRecentSuccessfulBuildsPerProject >= 0)
        {
            var overflowBuilds = prunableBuilds
                .Where(build => build.Status == BuildStatus.Succeeded)
                .GroupBy(build => build.ProjectId)
                .SelectMany(group => group
                    .OrderByDescending(build => build.FinishedAtUtc ?? build.QueuedAtUtc)
                    .Skip(appOptions.KeepRecentSuccessfulBuildsPerProject));

            foreach (var build in overflowBuilds)
            {
                buildsToPrune[build.Id] = build;
            }
        }

        if (appOptions.MaxBuildCacheSizeGb > 0)
        {
            var cacheLimitBytes = (long)appOptions.MaxBuildCacheSizeGb * 1024 * 1024 * 1024;
            var cacheEntries = prunableBuilds
                .Select(build => new BuildCacheEntry(build, GetDirectorySize(build.BuildRootPath)))
                .Where(entry => entry.SizeBytes > 0)
                .OrderBy(entry => entry.Build.FinishedAtUtc ?? entry.Build.QueuedAtUtc)
                .ToList();

            var currentCacheBytes = cacheEntries.Sum(entry => entry.SizeBytes);
            foreach (var entry in cacheEntries)
            {
                if (currentCacheBytes <= cacheLimitBytes)
                {
                    break;
                }

                if (buildsToPrune.ContainsKey(entry.Build.Id))
                {
                    currentCacheBytes -= entry.SizeBytes;
                    continue;
                }

                buildsToPrune[entry.Build.Id] = entry.Build;
                currentCacheBytes -= entry.SizeBytes;
            }
        }

        if (buildsToPrune.Count == 0)
        {
            return;
        }

        foreach (var build in buildsToPrune.Values)
        {
            DeleteDirectory(build.BuildRootPath);
            if (appOptions.CleanupArchiveDirectories)
            {
                DeleteDirectory(build.ArchiveDirectoryPath);
                build.ArchiveDirectoryPath = string.Empty;
            }

            build.DownloadUrl = null;
            build.ZipFilePath = null;
            build.LogFilePath = string.Empty;
            build.BuildRootPath = string.Empty;
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Pruned local build cache for {Count} finished builds. RetentionDays={RetentionDays}, KeepRecentSuccessfulBuildsPerProject={KeepRecent}, MaxBuildCacheSizeGb={MaxCacheGb}, CleanupArchiveDirectories={CleanupArchiveDirectories}",
            buildsToPrune.Count,
            appOptions.BuildRetentionDays,
            appOptions.KeepRecentSuccessfulBuildsPerProject,
            appOptions.MaxBuildCacheSizeGb,
            appOptions.CleanupArchiveDirectories);
    }

    private bool HasAnyCleanupRuleEnabled()
    {
        return appOptions.BuildRetentionDays > 0 ||
               appOptions.KeepRecentSuccessfulBuildsPerProject >= 0 ||
               appOptions.MaxBuildCacheSizeGb > 0;
    }

    private static bool HasPrunableFiles(BuildRecord build)
    {
        return !string.IsNullOrWhiteSpace(build.DownloadUrl) ||
               !string.IsNullOrWhiteSpace(build.ZipFilePath) ||
               !string.IsNullOrWhiteSpace(build.LogFilePath) ||
               !string.IsNullOrWhiteSpace(build.BuildRootPath) ||
               !string.IsNullOrWhiteSpace(build.ArchiveDirectoryPath);
    }

    private long GetDirectorySize(string? path)
    {
        try
        {
            return StoragePaths.GetDirectorySize(path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to calculate cache size for build directory {Path}", path);
            return 0;
        }
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

    private sealed record BuildCacheEntry(BuildRecord Build, long SizeBytes);
}
