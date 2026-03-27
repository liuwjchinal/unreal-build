using Backend.Data;
using Backend.Models;
using Backend.Options;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

public sealed class BuildScheduleService(
    IDbContextFactory<BuildDbContext> dbFactory,
    AppOptions appOptions,
    BuildScheduleRunner scheduleRunner,
    BuildScheduleRuntimeState runtimeState,
    ILogger<BuildScheduleService> logger) : BackgroundService
{
    private readonly TimeSpan _scanInterval = TimeSpan.FromSeconds(Math.Clamp(appOptions.ScheduleScanIntervalSeconds, 5, 55));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!appOptions.ScheduleServiceEnabled)
        {
            logger.LogInformation("Build schedule service is disabled.");
            runtimeState.LastScheduleTickUtc = DateTimeOffset.UtcNow;
            runtimeState.EnabledScheduleCount = 0;
            return;
        }

        logger.LogInformation("Build schedule service started. ScanIntervalSeconds={ScanIntervalSeconds}", _scanInterval.TotalSeconds);

        await ScanAsync(stoppingToken);

        using var timer = new PeriodicTimer(_scanInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await ScanAsync(stoppingToken);
        }
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var nowLocal = DateTime.Now;
        var localDate = nowLocal.ToString("yyyy-MM-dd");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var enabledSchedules = await db.Schedules
            .AsNoTracking()
            .Where(item => item.Enabled)
            .OrderBy(item => item.TimeOfDayLocal)
            .ThenBy(item => item.Name)
            .ToListAsync(cancellationToken);

        runtimeState.LastScheduleTickUtc = nowUtc;
        runtimeState.EnabledScheduleCount = enabledSchedules.Count;

        if (enabledSchedules.Count == 0)
        {
            return;
        }

        foreach (var schedule in enabledSchedules)
        {
            if (!IsDue(schedule, nowLocal, localDate))
            {
                continue;
            }

            logger.LogInformation(
                "Triggering scheduled build. ScheduleId={ScheduleId}, Name={ScheduleName}, ScopeType={ScopeType}, TimeOfDayLocal={TimeOfDayLocal}",
                schedule.Id,
                schedule.Name,
                schedule.ScopeType,
                schedule.TimeOfDayLocal);

            try
            {
                var result = await scheduleRunner.RunScheduledAsync(schedule.Id, nowUtc, cancellationToken);
                if (result is not null)
                {
                    logger.LogInformation(
                        "Scheduled build enqueue completed. ScheduleId={ScheduleId}, Requested={RequestedCount}, Enqueued={EnqueuedCount}, Failed={FailedCount}",
                        schedule.Id,
                        result.RequestedCount,
                        result.EnqueuedCount,
                        result.FailedCount);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled build trigger failed. ScheduleId={ScheduleId}", schedule.Id);
            }
        }
    }

    private static bool IsDue(BuildSchedule schedule, DateTime nowLocal, string localDate)
    {
        if (!TimeOnly.TryParseExact(schedule.TimeOfDayLocal, "HH:mm", out var timeOfDay))
        {
            return false;
        }

        if (string.Equals(schedule.LastTriggeredLocalDate, localDate, StringComparison.Ordinal))
        {
            return false;
        }

        return timeOfDay.Hour == nowLocal.Hour && timeOfDay.Minute == nowLocal.Minute;
    }
}
