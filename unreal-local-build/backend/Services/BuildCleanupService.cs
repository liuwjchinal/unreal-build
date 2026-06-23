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
            await CleanupOnceAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CleanupOnceAsync(CancellationToken cancellationToken)
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

        var buildCacheSizeBytesById = prunableBuilds.ToDictionary(
            build => build.Id,
            build => GetDirectorySize(build.BuildRootPath));
        var protectedBuildIds = GetProtectedRecentSuccessfulBuildIds(
            finishedBuilds,
            appOptions.KeepRecentSuccessfulBuildsPerProject);
        var buildIdsToPrune = SelectBuildIdsToPrune(
            prunableBuilds,
            buildCacheSizeBytesById,
            appOptions,
            DateTimeOffset.UtcNow,
            protectedBuildIds);
        var buildsToPrune = prunableBuilds
            .Where(build => buildIdsToPrune.Contains(build.Id))
            .ToList();

        if (buildsToPrune.Count == 0)
        {
            return;
        }

        foreach (var build in buildsToPrune)
        {
            DeleteDirectory(build.BuildRootPath);
            if (appOptions.CleanupArchiveDirectories)
            {
                DeleteDirectory(build.ArchiveDirectoryPath);
                build.ArchiveDirectoryPath = string.Empty;
            }

            build.DownloadUrl = null;
            build.ZipFilePath = null;
            build.AndroidPackageManifestPath = null;
            build.AndroidInstallScriptPath = null;
            build.LogFilePath = string.Empty;
            build.BuildRootPath = string.Empty;
        }

        await SqliteExecution.SaveChangesWithRetryAsync(db, logger, "build cleanup", cancellationToken);
        logger.LogInformation(
            "Pruned local build cache for {Count} finished builds. RetentionDays={RetentionDays}, KeepRecentSuccessfulBuildsPerProject={KeepRecent}, MaxBuildCacheSizeGb={MaxCacheGb}, CleanupArchiveDirectories={CleanupArchiveDirectories}",
            buildsToPrune.Count,
            appOptions.BuildRetentionDays,
            appOptions.KeepRecentSuccessfulBuildsPerProject,
            appOptions.MaxBuildCacheSizeGb,
            appOptions.CleanupArchiveDirectories);
    }

    public static HashSet<Guid> SelectBuildIdsToPrune(
        IReadOnlyCollection<BuildRecord> prunableBuilds,
        IReadOnlyDictionary<Guid, long> buildCacheSizeBytesById,
        AppOptions appOptions,
        DateTimeOffset nowUtc)
    {
        var protectedBuildIds = GetProtectedRecentSuccessfulBuildIds(
            prunableBuilds,
            appOptions.KeepRecentSuccessfulBuildsPerProject);
        return SelectBuildIdsToPrune(
            prunableBuilds,
            buildCacheSizeBytesById,
            appOptions,
            nowUtc,
            protectedBuildIds);
    }

    public static HashSet<Guid> SelectBuildIdsToPrune(
        IReadOnlyCollection<BuildRecord> prunableBuilds,
        IReadOnlyDictionary<Guid, long> buildCacheSizeBytesById,
        AppOptions appOptions,
        DateTimeOffset nowUtc,
        IReadOnlySet<Guid> protectedBuildIds)
    {
        var buildsToPrune = new HashSet<Guid>();
        var retentionCutoffUtc = nowUtc.AddDays(-appOptions.BuildRetentionDays);

        if (appOptions.BuildRetentionDays > 0)
        {
            foreach (var build in prunableBuilds.Where(build =>
                         build.FinishedAtUtc < retentionCutoffUtc &&
                         !protectedBuildIds.Contains(build.Id)))
            {
                buildsToPrune.Add(build.Id);
            }
        }

        if (appOptions.KeepRecentSuccessfulBuildsPerProject >= 0)
        {
            foreach (var build in prunableBuilds.Where(build =>
                         build.Status == BuildStatus.Succeeded &&
                         !protectedBuildIds.Contains(build.Id)))
            {
                buildsToPrune.Add(build.Id);
            }
        }

        if (appOptions.MaxBuildCacheSizeGb > 0)
        {
            var cacheLimitBytes = (long)appOptions.MaxBuildCacheSizeGb * 1024 * 1024 * 1024;
            var cacheEntries = prunableBuilds
                .Select(build => new BuildCacheEntry(
                    build,
                    buildCacheSizeBytesById.TryGetValue(build.Id, out var sizeBytes) ? sizeBytes : 0))
                .Where(entry => entry.SizeBytes > 0)
                .OrderBy(entry => entry.Build.FinishedAtUtc ?? entry.Build.QueuedAtUtc)
                .ThenBy(entry => entry.Build.Id)
                .ToList();

            var currentCacheBytes = cacheEntries.Sum(entry => entry.SizeBytes);
            foreach (var entry in cacheEntries)
            {
                if (currentCacheBytes <= cacheLimitBytes)
                {
                    break;
                }

                if (protectedBuildIds.Contains(entry.Build.Id))
                {
                    continue;
                }

                buildsToPrune.Add(entry.Build.Id);
                currentCacheBytes -= entry.SizeBytes;
            }
        }

        return buildsToPrune;
    }

    private bool HasAnyCleanupRuleEnabled()
    {
        return appOptions.BuildRetentionDays > 0 ||
               appOptions.KeepRecentSuccessfulBuildsPerProject >= 0 ||
               appOptions.MaxBuildCacheSizeGb > 0;
    }

    private static HashSet<Guid> GetProtectedRecentSuccessfulBuildIds(
        IReadOnlyCollection<BuildRecord> prunableBuilds,
        int keepRecentSuccessfulBuildsPerProject)
    {
        if (keepRecentSuccessfulBuildsPerProject <= 0)
        {
            return new HashSet<Guid>();
        }

        return prunableBuilds
            .Where(build => build.Status == BuildStatus.Succeeded)
            .GroupBy(build => build.ProjectId)
            .SelectMany(group => group
                .OrderByDescending(build => build.FinishedAtUtc ?? build.QueuedAtUtc)
                .ThenByDescending(build => build.Id)
                .Take(keepRecentSuccessfulBuildsPerProject))
            .Select(build => build.Id)
            .ToHashSet();
    }

    private bool HasPrunableFiles(BuildRecord build)
    {
        return !string.IsNullOrWhiteSpace(build.DownloadUrl) ||
               !string.IsNullOrWhiteSpace(build.ZipFilePath) ||
               !string.IsNullOrWhiteSpace(build.AndroidPackageManifestPath) ||
               !string.IsNullOrWhiteSpace(build.AndroidInstallScriptPath) ||
               !string.IsNullOrWhiteSpace(build.LogFilePath) ||
               !string.IsNullOrWhiteSpace(build.BuildRootPath) ||
               (appOptions.CleanupArchiveDirectories && !string.IsNullOrWhiteSpace(build.ArchiveDirectoryPath));
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
