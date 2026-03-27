using Backend.Models;
using Backend.Services;

namespace Backend.Contracts;

public sealed record QueueBuildRequest(
    Guid ProjectId,
    string Revision,
    BuildTargetType TargetType,
    string BuildConfiguration,
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
    BuildTargetType TargetType,
    string TargetName,
    string BuildConfiguration,
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

public sealed record BuildDetailDto(
    Guid Id,
    Guid ProjectId,
    string ProjectName,
    string Revision,
    BuildTriggerSource TriggerSource,
    Guid? ScheduleId,
    BuildTargetType TargetType,
    string TargetName,
    string BuildConfiguration,
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
    string? UatCommandPreview);

public sealed record BuildLogSnapshotDto(
    IReadOnlyList<string> Lines,
    int IncludedLines,
    long TotalLines,
    bool Truncated);

public static class BuildContractMappings
{
    public static BuildSummaryDto ToSummaryDto(this BuildRecord build)
    {
        return new BuildSummaryDto(
            build.Id,
            build.ProjectId,
            build.Project?.Name ?? string.Empty,
            build.DisplayRevision,
            build.TriggerSource,
            build.ScheduleId,
            build.TargetType,
            build.TargetName,
            build.BuildConfiguration,
            build.Status,
            build.CurrentPhase,
            build.ProgressPercent,
            build.StatusMessage,
            build.QueuedAtUtc,
            build.StartedAtUtc,
            build.FinishedAtUtc,
            GetDurationSeconds(build),
            build.ErrorSummary,
            GetAvailableDownloadUrl(build));
    }

    public static BuildDetailDto ToDetailDto(this BuildRecord build)
    {
        return new BuildDetailDto(
            build.Id,
            build.ProjectId,
            build.Project?.Name ?? string.Empty,
            build.DisplayRevision,
            build.TriggerSource,
            build.ScheduleId,
            build.TargetType,
            build.TargetName,
            build.BuildConfiguration,
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
            GetAvailableDownloadUrl(build),
            build.LogLineCount,
            BuildCommandFactory.CreateSvnPreview(build),
            BuildCommandFactory.CreateUatPreview(build));
    }

    private static string? GetAvailableDownloadUrl(BuildRecord build)
    {
        if (build.Status != BuildStatus.Succeeded ||
            string.IsNullOrWhiteSpace(build.DownloadUrl) ||
            string.IsNullOrWhiteSpace(build.ZipFilePath) ||
            !File.Exists(build.ZipFilePath) ||
            !CanReadFile(build.ZipFilePath))
        {
            return null;
        }

        return build.DownloadUrl;
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
