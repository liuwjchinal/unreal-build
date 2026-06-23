using Backend.Models;
using Backend.Options;
using Backend.Services;
using Xunit;

namespace Backend.Tests;

public sealed class BuildCleanupServiceTests
{
    [Fact]
    public void SelectBuildIdsToPrune_ProtectsRecentSuccessfulBuilds_FromCacheSizeLimit()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var projectId = Guid.NewGuid();
        var builds = Enumerable.Range(0, 35)
            .Select(index => CreateSucceededBuild(projectId, nowUtc.AddMinutes(-index)))
            .ToList();
        var oneGb = 1024L * 1024L * 1024L;
        var cacheSizes = builds.ToDictionary(build => build.Id, _ => oneGb);

        var prunedIds = BuildCleanupService.SelectBuildIdsToPrune(
            builds,
            cacheSizes,
            new AppOptions
            {
                BuildRetentionDays = 0,
                KeepRecentSuccessfulBuildsPerProject = 30,
                MaxBuildCacheSizeGb = 2
            },
            nowUtc);

        var protectedBuilds = builds
            .OrderByDescending(build => build.FinishedAtUtc)
            .Take(30)
            .ToList();
        var overflowBuilds = builds
            .OrderByDescending(build => build.FinishedAtUtc)
            .Skip(30)
            .ToList();

        Assert.Equal(5, prunedIds.Count);
        Assert.DoesNotContain(protectedBuilds, build => prunedIds.Contains(build.Id));
        Assert.All(overflowBuilds, build => Assert.Contains(build.Id, prunedIds));
    }

    [Fact]
    public void SelectBuildIdsToPrune_ProtectsRecentSuccessfulBuilds_FromRetentionDays()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var projectId = Guid.NewGuid();
        var builds = Enumerable.Range(0, 32)
            .Select(index => CreateSucceededBuild(projectId, nowUtc.AddDays(-31 - index)))
            .ToList();
        var cacheSizes = builds.ToDictionary(build => build.Id, _ => 1L);

        var prunedIds = BuildCleanupService.SelectBuildIdsToPrune(
            builds,
            cacheSizes,
            new AppOptions
            {
                BuildRetentionDays = 14,
                KeepRecentSuccessfulBuildsPerProject = 30,
                MaxBuildCacheSizeGb = 0
            },
            nowUtc);

        var protectedBuilds = builds
            .OrderByDescending(build => build.FinishedAtUtc)
            .Take(30)
            .ToList();
        var overflowBuilds = builds
            .OrderByDescending(build => build.FinishedAtUtc)
            .Skip(30)
            .ToList();

        Assert.Equal(2, prunedIds.Count);
        Assert.DoesNotContain(protectedBuilds, build => prunedIds.Contains(build.Id));
        Assert.All(overflowBuilds, build => Assert.Contains(build.Id, prunedIds));
    }

    [Fact]
    public void SelectBuildIdsToPrune_UsesStableTieBreaker_ForSameFinishedTime()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var projectId = Guid.NewGuid();
        var builds = Enumerable.Range(0, 32)
            .Select(_ => CreateSucceededBuild(projectId, nowUtc))
            .ToList();
        var cacheSizes = builds.ToDictionary(build => build.Id, _ => 1L);

        var prunedIds = BuildCleanupService.SelectBuildIdsToPrune(
            builds,
            cacheSizes,
            new AppOptions
            {
                BuildRetentionDays = 0,
                KeepRecentSuccessfulBuildsPerProject = 30,
                MaxBuildCacheSizeGb = 0
            },
            nowUtc);

        var expectedPrunedIds = builds
            .OrderByDescending(build => build.FinishedAtUtc ?? build.QueuedAtUtc)
            .ThenByDescending(build => build.Id)
            .Skip(30)
            .Select(build => build.Id)
            .ToHashSet();

        Assert.Equal(expectedPrunedIds, prunedIds);
    }

    [Fact]
    public void SelectBuildIdsToPrune_UsesAllSuccessfulBuilds_ForProtectedSet()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var projectId = Guid.NewGuid();
        var prunableBuild = CreateSucceededBuild(projectId, nowUtc.AddHours(-1));
        var newerAlreadyCleanedBuilds = Enumerable.Range(0, 30)
            .Select(index => CreateSucceededBuild(projectId, nowUtc.AddMinutes(index)))
            .ToList();
        var protectedBuildIds = newerAlreadyCleanedBuilds
            .OrderByDescending(build => build.FinishedAtUtc ?? build.QueuedAtUtc)
            .ThenByDescending(build => build.Id)
            .Take(30)
            .Select(build => build.Id)
            .ToHashSet();

        var prunedIds = BuildCleanupService.SelectBuildIdsToPrune(
            [prunableBuild],
            new Dictionary<Guid, long> { [prunableBuild.Id] = 1L },
            new AppOptions
            {
                BuildRetentionDays = 0,
                KeepRecentSuccessfulBuildsPerProject = 30,
                MaxBuildCacheSizeGb = 0
            },
            nowUtc,
            protectedBuildIds);

        Assert.Contains(prunableBuild.Id, prunedIds);
    }

    private static BuildRecord CreateSucceededBuild(Guid projectId, DateTimeOffset finishedAtUtc)
    {
        var id = Guid.NewGuid();
        return new BuildRecord
        {
            Id = id,
            ProjectId = projectId,
            Status = BuildStatus.Succeeded,
            QueuedAtUtc = finishedAtUtc.AddMinutes(-10),
            StartedAtUtc = finishedAtUtc.AddMinutes(-9),
            FinishedAtUtc = finishedAtUtc,
            BuildRootPath = $"build-{id:N}",
            ZipFilePath = $"build-{id:N}.zip",
            DownloadUrl = $"/api/builds/{id}/download"
        };
    }
}
