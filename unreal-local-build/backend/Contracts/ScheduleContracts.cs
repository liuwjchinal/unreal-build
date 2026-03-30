using Backend.Models;

namespace Backend.Contracts;

public sealed record UpsertBuildScheduleRequest(
    string Name,
    bool Enabled,
    BuildScheduleScopeType ScopeType,
    Guid? ProjectId,
    string TimeOfDayLocal,
    BuildPlatform Platform,
    BuildTargetType TargetType,
    string BuildConfiguration,
    bool Clean,
    bool Pak,
    bool IoStore,
    List<string>? ExtraUatArgs);

public sealed record BuildScheduleSummaryDto(
    Guid Id,
    string Name,
    bool Enabled,
    BuildScheduleScopeType ScopeType,
    Guid? ProjectId,
    string? ProjectName,
    string TimeOfDayLocal,
    BuildPlatform Platform,
    BuildTargetType TargetType,
    string BuildConfiguration,
    bool Clean,
    bool Pak,
    bool IoStore,
    DateTimeOffset? LastTriggeredAtUtc,
    int LastTriggeredBuildCount,
    string? LastTriggerMessage,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record BuildScheduleDetailDto(
    Guid Id,
    string Name,
    bool Enabled,
    BuildScheduleScopeType ScopeType,
    Guid? ProjectId,
    string? ProjectName,
    string TimeOfDayLocal,
    BuildPlatform Platform,
    BuildTargetType TargetType,
    string BuildConfiguration,
    bool Clean,
    bool Pak,
    bool IoStore,
    IReadOnlyList<string> ExtraUatArgs,
    DateTimeOffset? LastTriggeredAtUtc,
    string? LastTriggeredLocalDate,
    int LastTriggeredBuildCount,
    string? LastTriggerMessage,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record BuildScheduleRunResultDto(
    Guid ScheduleId,
    int RequestedCount,
    int EnqueuedCount,
    int FailedCount,
    string Message);

public static class ScheduleContractMappings
{
    public static BuildSchedule ToEntity(this UpsertBuildScheduleRequest request)
    {
        var schedule = new BuildSchedule();
        request.Apply(schedule);
        schedule.CreatedAtUtc = DateTimeOffset.UtcNow;
        schedule.UpdatedAtUtc = DateTimeOffset.UtcNow;
        return schedule;
    }

    public static void Apply(this UpsertBuildScheduleRequest request, BuildSchedule schedule)
    {
        schedule.Name = request.Name.Trim();
        schedule.Enabled = request.Enabled;
        schedule.ScopeType = request.ScopeType;
        schedule.ProjectId = request.ScopeType == BuildScheduleScopeType.SingleProject ? request.ProjectId : null;
        schedule.TimeOfDayLocal = request.TimeOfDayLocal.Trim();
        schedule.Platform = request.Platform;
        schedule.TargetType = request.TargetType;
        schedule.BuildConfiguration = request.BuildConfiguration.Trim();
        schedule.Clean = request.Clean;
        schedule.Pak = request.Pak;
        schedule.IoStore = request.IoStore;
        schedule.ExtraUatArgs = NormalizeList(request.ExtraUatArgs);
        schedule.UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    public static BuildScheduleSummaryDto ToSummaryDto(this BuildSchedule schedule)
    {
        return new BuildScheduleSummaryDto(
            schedule.Id,
            schedule.Name,
            schedule.Enabled,
            schedule.ScopeType,
            schedule.ProjectId,
            schedule.Project?.Name,
            schedule.TimeOfDayLocal,
            schedule.Platform,
            schedule.TargetType,
            schedule.BuildConfiguration,
            schedule.Clean,
            schedule.Pak,
            schedule.IoStore,
            schedule.LastTriggeredAtUtc,
            schedule.LastTriggeredBuildCount,
            schedule.LastTriggerMessage,
            schedule.CreatedAtUtc,
            schedule.UpdatedAtUtc);
    }

    public static BuildScheduleDetailDto ToDetailDto(this BuildSchedule schedule)
    {
        return new BuildScheduleDetailDto(
            schedule.Id,
            schedule.Name,
            schedule.Enabled,
            schedule.ScopeType,
            schedule.ProjectId,
            schedule.Project?.Name,
            schedule.TimeOfDayLocal,
            schedule.Platform,
            schedule.TargetType,
            schedule.BuildConfiguration,
            schedule.Clean,
            schedule.Pak,
            schedule.IoStore,
            schedule.ExtraUatArgs,
            schedule.LastTriggeredAtUtc,
            schedule.LastTriggeredLocalDate,
            schedule.LastTriggeredBuildCount,
            schedule.LastTriggerMessage,
            schedule.CreatedAtUtc,
            schedule.UpdatedAtUtc);
    }

    private static List<string> NormalizeList(IEnumerable<string>? values)
    {
        return (values ?? Array.Empty<string>())
            .Select(item => item.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
