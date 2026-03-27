using Backend.Contracts;
using Backend.Data;
using Backend.Models;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

public sealed class BuildScheduleRunner(
    IDbContextFactory<BuildDbContext> dbFactory,
    BuildOrchestrator orchestrator,
    ILogger<BuildScheduleRunner> logger)
{
    public Task<BuildScheduleRunResultDto?> RunNowAsync(Guid scheduleId, CancellationToken cancellationToken)
    {
        return RunInternalAsync(scheduleId, DateTimeOffset.UtcNow, persistScheduleDate: false, cancellationToken);
    }

    public Task<BuildScheduleRunResultDto?> RunScheduledAsync(
        Guid scheduleId,
        DateTimeOffset triggeredAtUtc,
        CancellationToken cancellationToken)
    {
        return RunInternalAsync(scheduleId, triggeredAtUtc, persistScheduleDate: true, cancellationToken);
    }

    private async Task<BuildScheduleRunResultDto?> RunInternalAsync(
        Guid scheduleId,
        DateTimeOffset triggeredAtUtc,
        bool persistScheduleDate,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var schedule = await db.Schedules
            .AsNoTracking()
            .Include(item => item.Project)
            .FirstOrDefaultAsync(item => item.Id == scheduleId, cancellationToken);

        if (schedule is null)
        {
            return null;
        }

        var localDate = DateTime.Now.ToString("yyyy-MM-dd");
        if (persistScheduleDate)
        {
            await UpdateScheduleStateAsync(
                scheduleId,
                triggeredAtUtc,
                localDate,
                0,
                "定时任务已触发，正在加入构建队列。",
                cancellationToken);
        }

        var projects = await ResolveProjectsAsync(schedule, cancellationToken);
        var requestedCount = projects.Count;
        var enqueuedCount = 0;
        var failedCount = 0;

        foreach (var project in projects)
        {
            var request = new QueueBuildRequest(
                project.Id,
                "HEAD",
                schedule.TargetType,
                schedule.BuildConfiguration,
                schedule.Clean,
                schedule.Pak,
                schedule.IoStore,
                schedule.ExtraUatArgs);

            try
            {
                await orchestrator.EnqueueBuildAsync(
                    request,
                    new BuildEnqueueMetadata(BuildTriggerSource.Schedule, schedule.Id),
                    cancellationToken);
                enqueuedCount++;
            }
            catch (ProjectNotFoundException)
            {
                failedCount++;
                logger.LogWarning("Schedule {ScheduleId} skipped missing project {ProjectId}.", schedule.Id, project.Id);
            }
            catch (BuildValidationException ex)
            {
                failedCount++;
                logger.LogWarning(
                    "Schedule {ScheduleId} failed to enqueue project {ProjectId} due to validation error: {Errors}",
                    schedule.Id,
                    project.Id,
                    string.Join("; ", ex.Errors.SelectMany(pair => pair.Value).Distinct()));
            }
            catch (Exception ex)
            {
                failedCount++;
                logger.LogError(ex, "Schedule {ScheduleId} failed to enqueue project {ProjectId}.", schedule.Id, project.Id);
            }
        }

        var message = requestedCount == 0
            ? "当前没有可触发的项目。"
            : $"请求 {requestedCount} 个项目，成功入队 {enqueuedCount} 个，失败 {failedCount} 个。";

        await UpdateScheduleRunResultAsync(
            scheduleId,
            triggeredAtUtc,
            persistScheduleDate ? localDate : null,
            enqueuedCount,
            message,
            persistScheduleDate,
            cancellationToken);

        return new BuildScheduleRunResultDto(scheduleId, requestedCount, enqueuedCount, failedCount, message);
    }

    private async Task<List<ProjectConfig>> ResolveProjectsAsync(BuildSchedule schedule, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        if (schedule.ScopeType == BuildScheduleScopeType.SingleProject)
        {
            if (!schedule.ProjectId.HasValue)
            {
                return new List<ProjectConfig>();
            }

            var project = await db.Projects
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == schedule.ProjectId.Value, cancellationToken);

            return project is null ? new List<ProjectConfig>() : new List<ProjectConfig> { project };
        }

        return await db.Projects
            .AsNoTracking()
            .OrderBy(item => item.Name)
            .ToListAsync(cancellationToken);
    }

    private async Task UpdateScheduleStateAsync(
        Guid scheduleId,
        DateTimeOffset triggeredAtUtc,
        string localDate,
        int buildCount,
        string message,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var schedule = await db.Schedules.FirstOrDefaultAsync(item => item.Id == scheduleId, cancellationToken);
        if (schedule is null)
        {
            return;
        }

        schedule.LastTriggeredAtUtc = triggeredAtUtc;
        schedule.LastTriggeredLocalDate = localDate;
        schedule.LastTriggeredBuildCount = buildCount;
        schedule.LastTriggerMessage = message;
        schedule.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await SqliteExecution.SaveChangesWithRetryAsync(db, logger, "mark schedule trigger", cancellationToken);
    }

    private async Task UpdateScheduleRunResultAsync(
        Guid scheduleId,
        DateTimeOffset triggeredAtUtc,
        string? localDate,
        int buildCount,
        string message,
        bool persistScheduleDate,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var schedule = await db.Schedules.FirstOrDefaultAsync(item => item.Id == scheduleId, cancellationToken);
        if (schedule is null)
        {
            return;
        }

        schedule.LastTriggeredAtUtc = triggeredAtUtc;
        if (persistScheduleDate && localDate is not null)
        {
            schedule.LastTriggeredLocalDate = localDate;
        }

        schedule.LastTriggeredBuildCount = buildCount;
        schedule.LastTriggerMessage = message;
        schedule.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await SqliteExecution.SaveChangesWithRetryAsync(db, logger, "update schedule result", cancellationToken);
    }
}
