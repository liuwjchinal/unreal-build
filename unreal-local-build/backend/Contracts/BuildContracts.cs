using Backend.Models;
using Backend.Services;

namespace Backend.Contracts;

public sealed record QueueBuildRequest(
    Guid ProjectId,
    string Revision,
    BuildPlatform Platform,
    BuildTargetType TargetType,
    string BuildConfiguration,
    BuildAccelerator? BuildAccelerator,
    AndroidPackagingMode? AndroidPackagingMode,
    bool Clean,
    bool Pak,
    bool IoStore,
    List<string>? ExtraUatArgs);

public sealed record BuildSummaryDto(
    Guid Id,
    Guid ProjectId,
    string ProjectName,
    string Revision,
    BuildTriggerSource TriggerSource,
    Guid? ScheduleId,
    BuildPlatform Platform,
    BuildTargetType TargetType,
    string TargetName,
    string BuildConfiguration,
    BuildAccelerator BuildAccelerator,
    AndroidPackagingMode AndroidPackagingMode,
    BuildStatus Status,
    BuildPhase CurrentPhase,
    int ProgressPercent,
    string StatusMessage,
    DateTimeOffset QueuedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    long? DurationSeconds,
    string? ErrorSummary,
    string? DownloadUrl);

public sealed record AndroidPackageArtifactDto(
    string ProjectName,
    string PackageName,
    string PackagingMode,
    string ApkPath,
    string DataRoot,
    long ApkSizeBytes,
    long TotalDataSizeBytes,
    int FileCount,
    string GeneratedAtUtc,
    string InstallerDownloadUrl,
    string ManifestDownloadUrl);

public sealed record BuildDetailDto(
    Guid Id,
    Guid ProjectId,
    string ProjectName,
    string Revision,
    BuildTriggerSource TriggerSource,
    Guid? ScheduleId,
    BuildPlatform Platform,
    BuildTargetType TargetType,
    string TargetName,
    string BuildConfiguration,
    BuildAccelerator BuildAccelerator,
    AndroidPackagingMode AndroidPackagingMode,
    bool Clean,
    bool Pak,
    bool IoStore,
    IReadOnlyList<string> ExtraUatArgs,
    BuildStatus Status,
    BuildPhase CurrentPhase,
    int ProgressPercent,
    string StatusMessage,
    DateTimeOffset QueuedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    long? DurationSeconds,
    int? ExitCode,
    string? ErrorSummary,
    string? DownloadUrl,
    long LogLineCount,
    string? SvnCommandPreview,
    string? UatCommandPreview,
    bool UbaRemoteEnabled,
    string? UbaHost,
    string? UbaListenHost,
    int? UbaPort,
    int? UbaAgentMaxIdleSeconds,
    int? UbaAgentStoreCapacityGb,
    int? UbaMaxWorkers,
    string? UbaAgentJoinUrl,
    string? UbaAgentManualCommand,
    bool UbaHostAutoDetected,
    string? UbaHostWarning,
    AndroidPackageArtifactDto? AndroidPackage);

public sealed record BuildLogSnapshotDto(
    IReadOnlyList<string> Lines,
    int IncludedLines,
    long TotalLines,
    bool Truncated);

public static class BuildContractMappings
{
    public static BuildSummaryDto ToSummaryDto(this BuildRecord build)
    {
        return build.ToSummaryDto(allowArchiveFallbackDownload: false);
    }

    public static BuildSummaryDto ToSummaryDto(this BuildRecord build, bool allowArchiveFallbackDownload)
    {
        return new BuildSummaryDto(
            build.Id,
            build.ProjectId,
            build.Project?.Name ?? string.Empty,
            build.DisplayRevision,
            build.TriggerSource,
            build.ScheduleId,
            build.Platform,
            build.TargetType,
            build.TargetName,
            build.BuildConfiguration,
            build.BuildAccelerator,
            build.AndroidPackagingMode,
            build.Status,
            build.CurrentPhase,
            build.ProgressPercent,
            build.StatusMessage,
            build.QueuedAtUtc,
            build.StartedAtUtc,
            build.FinishedAtUtc,
            GetDurationSeconds(build),
            build.ErrorSummary,
            GetAvailableDownloadUrl(build, allowArchiveFallbackDownload));
    }

    public static BuildDetailDto ToDetailDto(this BuildRecord build)
    {
        return build.ToDetailDto(allowArchiveFallbackDownload: false);
    }

    public static BuildDetailDto ToDetailDto(this BuildRecord build, bool allowArchiveFallbackDownload)
    {
        return new BuildDetailDto(
            build.Id,
            build.ProjectId,
            build.Project?.Name ?? string.Empty,
            build.DisplayRevision,
            build.TriggerSource,
            build.ScheduleId,
            build.Platform,
            build.TargetType,
            build.TargetName,
            build.BuildConfiguration,
            build.BuildAccelerator,
            build.AndroidPackagingMode,
            build.Clean,
            build.Pak,
            build.IoStore,
            build.ExtraUatArgs,
            build.Status,
            build.CurrentPhase,
            build.ProgressPercent,
            build.StatusMessage,
            build.QueuedAtUtc,
            build.StartedAtUtc,
            build.FinishedAtUtc,
            GetDurationSeconds(build),
            build.ExitCode,
            build.ErrorSummary,
            GetAvailableDownloadUrl(build, allowArchiveFallbackDownload),
            build.LogLineCount,
            BuildCommandFactory.CreateSvnPreview(build),
            BuildCommandFactory.CreateUatPreview(build),
            build.UbaRemoteEnabled,
            build.UbaHost,
            build.UbaListenHost,
            build.UbaPort,
            build.UbaAgentMaxIdleSeconds,
            build.UbaAgentStoreCapacityGb,
            build.UbaMaxWorkers,
            build.UbaAgentJoinUrl,
            build.UbaAgentManualCommand,
            build.UbaHostAutoDetected,
            build.UbaHostWarning,
            GetAndroidPackageArtifact(build));
    }

    private static AndroidPackageArtifactDto? GetAndroidPackageArtifact(BuildRecord build)
    {
        var manifest = AndroidPackageArtifactsService.TryReadManifest(build.AndroidPackageManifestPath);
        if (manifest is null ||
            string.IsNullOrWhiteSpace(build.AndroidInstallScriptPath) ||
            !File.Exists(build.AndroidInstallScriptPath))
        {
            return null;
        }

        return new AndroidPackageArtifactDto(
            manifest.ProjectName,
            manifest.PackageName,
            manifest.PackagingMode,
            manifest.ApkPath,
            manifest.DataRoot,
            manifest.ApkSizeBytes,
            manifest.TotalDataSizeBytes,
            manifest.Files.Count,
            manifest.GeneratedAtUtc,
            $"/api/builds/{build.Id}/android-package/installer",
            $"/api/builds/{build.Id}/android-package/manifest");
    }

    private static string? GetAvailableDownloadUrl(BuildRecord build, bool allowArchiveFallbackDownload)
    {
        if (build.Status != BuildStatus.Succeeded)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(build.ZipFilePath) &&
            File.Exists(build.ZipFilePath) &&
            CanReadFile(build.ZipFilePath))
        {
            return BuildDownloadUrl(build);
        }

        return allowArchiveFallbackDownload && HasArchiveDirectory(build.ArchiveDirectoryPath)
            ? BuildDownloadUrl(build)
            : null;
    }

    private static string BuildDownloadUrl(BuildRecord build)
    {
        return string.IsNullOrWhiteSpace(build.DownloadUrl)
            ? $"/api/builds/{build.Id}/download"
            : build.DownloadUrl;
    }

    private static bool HasArchiveDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return false;
        }

        try
        {
            return Directory.EnumerateFileSystemEntries(path).Any();
        }
        catch
        {
            return false;
        }
    }

    private static bool CanReadFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return stream.Length >= 0;
        }
        catch
        {
            return false;
        }
    }

    private static long? GetDurationSeconds(BuildRecord build)
    {
        var start = build.StartedAtUtc ?? build.QueuedAtUtc;
        var end = build.FinishedAtUtc ?? (build.Status == BuildStatus.Running ? DateTimeOffset.UtcNow : (DateTimeOffset?)null);
        return end.HasValue ? Convert.ToInt64(Math.Max(0, (end.Value - start).TotalSeconds)) : null;
    }
}
