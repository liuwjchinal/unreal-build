using System.Text.Json;
using System.Text.Json.Serialization;
using Backend.Contracts;
using Backend.Data;
using Backend.Models;
using Backend.Options;
using Backend.Services;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var appOptions = builder.Configuration.GetSection(AppOptions.SectionName).Get<AppOptions>() ?? new AppOptions();
var storagePaths = StoragePaths.Create(appOptions, builder.Environment.ContentRootPath);

builder.WebHost.UseUrls(appOptions.ServerUrl);

builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.WriteIndented = true;
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("dev-client", policy =>
    {
        policy.WithOrigins(appOptions.FrontendDevOrigin)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services.AddSingleton(appOptions);
builder.Services.AddSingleton(storagePaths);
builder.Services.AddSingleton<BuildEventBroker>();
builder.Services.AddSingleton<AutomationToolJanitor>();
builder.Services.AddSingleton<ProjectValidator>();
builder.Services.AddSingleton<BuildLogReader>();
builder.Services.AddSingleton<BuildLogAnalyzer>();
builder.Services.AddSingleton<BuildScheduleRuntimeState>();
builder.Services.AddSingleton<BuildScheduleValidator>();
builder.Services.AddSingleton<BuildScheduleRunner>();
builder.Services.AddSingleton<BuildScheduleService>();
builder.Services.AddSingleton<BuildOrchestrator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BuildOrchestrator>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<BuildScheduleService>());
builder.Services.AddHostedService<BuildCleanupService>();
builder.Services.AddDbContextFactory<BuildDbContext>(options =>
{
    options.UseSqlite(SqliteExecution.BuildConnectionString(storagePaths.DatabasePath));
});

var app = builder.Build();

storagePaths.EnsureCreated();

await using (var db = await app.Services.GetRequiredService<IDbContextFactory<BuildDbContext>>().CreateDbContextAsync())
{
    await DatabaseMigrator.MigrateAsync(db, CancellationToken.None);
    await SqliteExecution.ConfigureDatabaseAsync(db, CancellationToken.None);
}

if (app.Environment.IsDevelopment())
{
    app.UseCors("dev-client");
}

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapGet("/health", (BuildScheduleRuntimeState scheduleRuntimeState) =>
{
    var buildCacheSizeBytes = storagePaths.GetBuildCacheSizeBytes();
    var buildCacheSizeGb = Math.Round(buildCacheSizeBytes / 1024d / 1024d / 1024d, 2);

    return Results.Ok(new
    {
        ok = true,
        serverTimeUtc = DateTimeOffset.UtcNow,
        serverUrl = appOptions.ServerUrl,
        globalConcurrency = appOptions.GlobalConcurrency,
        uatConcurrency = appOptions.UatConcurrency,
        automationToolCleanupEnabled = appOptions.AutomationToolCleanupEnabled,
        automationToolCleanupMode = appOptions.AutomationToolCleanupMode,
        retentionDays = appOptions.BuildRetentionDays,
        cleanupIntervalMinutes = appOptions.CleanupIntervalMinutes,
        keepRecentSuccessfulBuildsPerProject = appOptions.KeepRecentSuccessfulBuildsPerProject,
        maxBuildCacheSizeGb = appOptions.MaxBuildCacheSizeGb,
        cleanupArchiveDirectories = appOptions.CleanupArchiveDirectories,
        scheduleServiceEnabled = appOptions.ScheduleServiceEnabled,
        scheduleScanIntervalSeconds = appOptions.ScheduleScanIntervalSeconds,
        enabledScheduleCount = scheduleRuntimeState.EnabledScheduleCount,
        lastScheduleTickUtc = scheduleRuntimeState.LastScheduleTickUtc,
        buildCacheDirectory = storagePaths.BuildsRootPath,
        buildCacheSizeBytes,
        buildCacheSizeGb
    });
});

api.MapGet("/projects", async (IDbContextFactory<BuildDbContext> dbFactory, CancellationToken cancellationToken) =>
{
    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var projects = await db.Projects
        .AsNoTracking()
        .OrderBy(project => project.Name)
        .ToListAsync(cancellationToken);

    return Results.Ok(projects.Select(project => project.ToSummaryDto()));
});

api.MapGet("/projects/export", async (IDbContextFactory<BuildDbContext> dbFactory, CancellationToken cancellationToken) =>
{
    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var payload = await db.Projects
        .AsNoTracking()
        .OrderBy(project => project.Name)
        .Select(project => new UpsertProjectRequest(
            project.ProjectKey,
            project.Name,
            project.WorkingCopyPath,
            project.UProjectPath,
            project.EngineRootPath,
            project.ArchiveRootPath,
            project.GameTarget,
            project.ClientTarget,
            project.ServerTarget,
            project.AllowedBuildConfigurations,
            project.DefaultExtraUatArgs))
        .ToListAsync(cancellationToken);

    var fileName = $"projects-export-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json";
    var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions
    {
        WriteIndented = true
    });

    return Results.File(bytes, "application/json", fileName);
});

api.MapPost("/projects/import", async (
    List<UpsertProjectRequest> requests,
    IDbContextFactory<BuildDbContext> dbFactory,
    ProjectValidator validator,
    CancellationToken cancellationToken) =>
{
    if (requests.Count == 0)
    {
        return Results.BadRequest(new { message = AppText.NoProjectsImported });
    }

    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var existingProjects = await db.Projects.ToListAsync(cancellationToken);
    var seenProjectKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var seenFingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    var created = 0;
    var updated = 0;
    var conflicts = new List<ImportProjectConflictDto>();

    foreach (var request in requests)
    {
        var resolvedProjectKey = ProjectIdentity.EnsureProjectKey(request.ProjectKey);
        var fingerprint = ProjectIdentity.CreateFingerprint(request.WorkingCopyPath, request.UProjectPath, request.EngineRootPath);
        var normalizedRequest = request with { ProjectKey = resolvedProjectKey };

        if (!seenProjectKeys.Add(resolvedProjectKey))
        {
            conflicts.Add(new ImportProjectConflictDto(
                request.Name,
                resolvedProjectKey,
                AppText.DuplicateProjectKeyInImportFile));
            continue;
        }

        if (!seenFingerprints.Add(fingerprint))
        {
            conflicts.Add(new ImportProjectConflictDto(
                request.Name,
                resolvedProjectKey,
                AppText.DuplicateProjectFingerprintInImportFile));
            continue;
        }

        var existingByKey = existingProjects.FirstOrDefault(project =>
            string.Equals(project.ProjectKey, resolvedProjectKey, StringComparison.OrdinalIgnoreCase));

        var sameFingerprintProjects = existingProjects
            .Where(project => string.Equals(project.ProjectFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();

        ProjectConfig? targetProject = existingByKey;
        if (targetProject is null)
        {
            if (sameFingerprintProjects.Count > 1)
            {
                conflicts.Add(new ImportProjectConflictDto(
                    request.Name,
                    resolvedProjectKey,
                    AppText.MultipleProjectsShareFingerprint));
                continue;
            }

            if (sameFingerprintProjects.Count == 1)
            {
                targetProject = sameFingerprintProjects[0];
            }
        }

        if (targetProject is null && string.IsNullOrWhiteSpace(request.ProjectKey))
        {
            var sameNameDifferentProject = existingProjects.Any(project =>
                string.Equals(project.Name, request.Name, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(project.ProjectFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase));

            if (sameNameDifferentProject)
            {
                conflicts.Add(new ImportProjectConflictDto(
                    request.Name,
                    resolvedProjectKey,
                    AppText.SameNameDifferentPathConflict));
                continue;
            }
        }

        var validationErrors = await validator.ValidateProjectAsync(normalizedRequest, targetProject?.Id, cancellationToken);
        if (validationErrors.Count > 0)
        {
            conflicts.Add(new ImportProjectConflictDto(
                request.Name,
                resolvedProjectKey,
                FlattenValidationErrors(validationErrors)));
            continue;
        }

        if (targetProject is null)
        {
            var createdProject = normalizedRequest.ToEntity();
            db.Projects.Add(createdProject);
            existingProjects.Add(createdProject);
            created++;
        }
        else
        {
            normalizedRequest.Apply(targetProject);
            updated++;
        }
    }

    await db.SaveChangesAsync(cancellationToken);

    return Results.Ok(new ImportProjectsResultDto(
        created,
        updated,
        conflicts.Count,
        requests.Count,
        conflicts));
});

api.MapGet("/projects/{id:guid}", async (Guid id, IDbContextFactory<BuildDbContext> dbFactory, CancellationToken cancellationToken) =>
{
    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var project = await db.Projects
        .AsNoTracking()
        .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

    return project is null ? Results.NotFound() : Results.Ok(project.ToSummaryDto());
});

api.MapGet("/projects/{id:guid}/config", async (Guid id, IDbContextFactory<BuildDbContext> dbFactory, CancellationToken cancellationToken) =>
{
    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var project = await db.Projects
        .AsNoTracking()
        .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

    return project is null ? Results.NotFound() : Results.Ok(project.ToConfigDto());
});

api.MapPost("/projects", async (
    UpsertProjectRequest request,
    IDbContextFactory<BuildDbContext> dbFactory,
    ProjectValidator validator,
    CancellationToken cancellationToken) =>
{
    var normalizedRequest = request with { ProjectKey = ProjectIdentity.EnsureProjectKey(request.ProjectKey) };
    var validationErrors = await validator.ValidateProjectAsync(normalizedRequest, null, cancellationToken);
    if (validationErrors.Count > 0)
    {
        return Results.ValidationProblem(validationErrors);
    }

    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var project = normalizedRequest.ToEntity();
    db.Projects.Add(project);
    await db.SaveChangesAsync(cancellationToken);

    return Results.Created($"/api/projects/{project.Id}", project.ToSummaryDto());
});

api.MapPut("/projects/{id:guid}", async (
    Guid id,
    UpsertProjectRequest request,
    IDbContextFactory<BuildDbContext> dbFactory,
    ProjectValidator validator,
    CancellationToken cancellationToken) =>
{
    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var project = await db.Projects.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
    if (project is null)
    {
        return Results.NotFound();
    }

    var normalizedRequest = request with { ProjectKey = project.ProjectKey };
    var validationErrors = await validator.ValidateProjectAsync(normalizedRequest, id, cancellationToken);
    if (validationErrors.Count > 0)
    {
        return Results.ValidationProblem(validationErrors);
    }

    normalizedRequest.Apply(project);
    await db.SaveChangesAsync(cancellationToken);

    return Results.Ok(project.ToSummaryDto());
});

api.MapDelete("/projects/{id:guid}", async (Guid id, IDbContextFactory<BuildDbContext> dbFactory, CancellationToken cancellationToken) =>
{
    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var project = await db.Projects.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
    if (project is null)
    {
        return Results.NotFound();
    }

    var hasActiveBuilds = await db.Builds.AnyAsync(build =>
        build.ProjectId == id &&
        (build.Status == BuildStatus.Queued || build.Status == BuildStatus.Running),
        cancellationToken);

    if (hasActiveBuilds)
    {
        return Results.Conflict(new { message = AppText.ActiveBuildsPreventDelete });
    }

    db.Projects.Remove(project);
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
});

api.MapGet("/schedules", async (IDbContextFactory<BuildDbContext> dbFactory, CancellationToken cancellationToken) =>
{
    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var schedules = await db.Schedules
        .AsNoTracking()
        .Include(item => item.Project)
        .OrderBy(item => item.TimeOfDayLocal)
        .ThenBy(item => item.Name)
        .ToListAsync(cancellationToken);

    return Results.Ok(schedules.Select(item => item.ToSummaryDto()));
});

api.MapGet("/schedules/{id:guid}", async (Guid id, IDbContextFactory<BuildDbContext> dbFactory, CancellationToken cancellationToken) =>
{
    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var schedule = await db.Schedules
        .AsNoTracking()
        .Include(item => item.Project)
        .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

    return schedule is null ? Results.NotFound() : Results.Ok(schedule.ToDetailDto());
});

api.MapPost("/schedules", async (
    UpsertBuildScheduleRequest request,
    IDbContextFactory<BuildDbContext> dbFactory,
    BuildScheduleValidator validator,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    var validationErrors = await validator.ValidateAsync(request, null, cancellationToken);
    if (validationErrors.Count > 0)
    {
        return Results.ValidationProblem(validationErrors);
    }

    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var schedule = request.ToEntity();
    db.Schedules.Add(schedule);
    await SqliteExecution.SaveChangesWithRetryAsync(db, logger, "create schedule", cancellationToken);
    await db.Entry(schedule).Reference(item => item.Project).LoadAsync(cancellationToken);

    return Results.Created($"/api/schedules/{schedule.Id}", schedule.ToDetailDto());
});

api.MapPut("/schedules/{id:guid}", async (
    Guid id,
    UpsertBuildScheduleRequest request,
    IDbContextFactory<BuildDbContext> dbFactory,
    BuildScheduleValidator validator,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    var validationErrors = await validator.ValidateAsync(request, id, cancellationToken);
    if (validationErrors.Count > 0)
    {
        return Results.ValidationProblem(validationErrors);
    }

    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var schedule = await db.Schedules
        .Include(item => item.Project)
        .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

    if (schedule is null)
    {
        return Results.NotFound();
    }

    request.Apply(schedule);
    await SqliteExecution.SaveChangesWithRetryAsync(db, logger, "update schedule", cancellationToken);
    await db.Entry(schedule).Reference(item => item.Project).LoadAsync(cancellationToken);

    return Results.Ok(schedule.ToDetailDto());
});

api.MapDelete("/schedules/{id:guid}", async (
    Guid id,
    IDbContextFactory<BuildDbContext> dbFactory,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var schedule = await db.Schedules.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
    if (schedule is null)
    {
        return Results.NotFound();
    }

    db.Schedules.Remove(schedule);
    await SqliteExecution.SaveChangesWithRetryAsync(db, logger, "delete schedule", cancellationToken);
    return Results.NoContent();
});

api.MapPost("/schedules/{id:guid}/toggle", async (
    Guid id,
    IDbContextFactory<BuildDbContext> dbFactory,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var schedule = await db.Schedules
        .Include(item => item.Project)
        .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

    if (schedule is null)
    {
        return Results.NotFound();
    }

    schedule.Enabled = !schedule.Enabled;
    schedule.UpdatedAtUtc = DateTimeOffset.UtcNow;
    await SqliteExecution.SaveChangesWithRetryAsync(db, logger, "toggle schedule", cancellationToken);
    return Results.Ok(schedule.ToSummaryDto());
});

api.MapPost("/schedules/{id:guid}/run-now", async (
    Guid id,
    BuildScheduleRunner runner,
    CancellationToken cancellationToken) =>
{
    var result = await runner.RunNowAsync(id, cancellationToken);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

api.MapPost("/builds", async (
    QueueBuildRequest request,
    BuildOrchestrator orchestrator,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    try
    {
        var build = await orchestrator.EnqueueBuildAsync(request, cancellationToken);
        return Results.Accepted($"/api/builds/{build.Id}", build.ToDetailDto());
    }
    catch (ProjectNotFoundException)
    {
        return Results.NotFound();
    }
    catch (BuildValidationException ex)
    {
        return Results.ValidationProblem(ex.Errors);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to enqueue build for project {ProjectId}.", request.ProjectId);
        return Results.Problem(
            title: "构建入队失败",
            detail: ex.Message,
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

api.MapPost("/builds/{id:guid}/cancel", async (
    Guid id,
    BuildOrchestrator orchestrator,
    IDbContextFactory<BuildDbContext> dbFactory,
    CancellationToken cancellationToken) =>
{
    var build = await orchestrator.CancelBuildAsync(id, cancellationToken);
    if (build is null)
    {
        return Results.NotFound();
    }

    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var freshBuild = await db.Builds
        .AsNoTracking()
        .Include(item => item.Project)
        .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

    return freshBuild is null ? Results.NotFound() : Results.Ok(freshBuild.ToDetailDto());
});

api.MapGet("/builds", async (
    IDbContextFactory<BuildDbContext> dbFactory,
    Guid? projectId,
    int? limit,
    CancellationToken cancellationToken) =>
{
    var take = Math.Clamp(limit ?? 50, 1, 200);

    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var query = db.Builds
        .AsNoTracking()
        .Include(build => build.Project)
        .AsQueryable();

    if (projectId.HasValue)
    {
        query = query.Where(build => build.ProjectId == projectId.Value);
    }

    var builds = await query.ToListAsync(cancellationToken);
    builds = builds
        .OrderByDescending(build => build.QueuedAtUtc)
        .Take(take)
        .ToList();

    return Results.Ok(builds.Select(build => build.ToSummaryDto()));
});

api.MapGet("/builds/{id:guid}", async (Guid id, IDbContextFactory<BuildDbContext> dbFactory, CancellationToken cancellationToken) =>
{
    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var build = await db.Builds
        .AsNoTracking()
        .Include(item => item.Project)
        .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

    return build is null ? Results.NotFound() : Results.Ok(build.ToDetailDto());
});

api.MapGet("/builds/{id:guid}/log", async (
    Guid id,
    int? tailLines,
    IDbContextFactory<BuildDbContext> dbFactory,
    BuildLogReader logReader,
    CancellationToken cancellationToken) =>
{
    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var build = await db.Builds
        .AsNoTracking()
        .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

    if (build is null)
    {
        return Results.NotFound();
    }

    var snapshot = await logReader.ReadAsync(
        build.LogFilePath,
        tailLines ?? appOptions.DefaultLogTailLines,
        build.LogLineCount,
        cancellationToken);
    return Results.Ok(snapshot);
});

api.MapGet("/builds/{id:guid}/download", async (
    Guid id,
    IDbContextFactory<BuildDbContext> dbFactory,
    CancellationToken cancellationToken) =>
{
    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var build = await db.Builds
        .AsNoTracking()
        .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

    if (build is null ||
        build.Status != BuildStatus.Succeeded ||
        string.IsNullOrWhiteSpace(build.ZipFilePath) ||
        !File.Exists(build.ZipFilePath))
    {
        return Results.NotFound();
    }

    try
    {
        using var stream = new FileStream(build.ZipFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }
    catch (IOException)
    {
        return Results.Problem("产物仍在生成，请稍后重试。", statusCode: StatusCodes.Status409Conflict);
    }

    return Results.File(
        build.ZipFilePath,
        "application/zip",
        Path.GetFileName(build.ZipFilePath),
        enableRangeProcessing: true);
});

api.MapGet("/events", async (
    HttpContext context,
    BuildEventBroker eventBroker,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken) =>
{
    var logger = loggerFactory.CreateLogger("BuildEvents");
    await WriteSseStreamAsync(context, eventBroker.Subscribe(null, cancellationToken), null, logger, cancellationToken);
});

api.MapGet("/builds/{id:guid}/events", async (
    Guid id,
    HttpContext context,
    BuildEventBroker eventBroker,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken) =>
{
    var logger = loggerFactory.CreateLogger("BuildEvents");
    await WriteSseStreamAsync(context, eventBroker.Subscribe(id, cancellationToken), id, logger, cancellationToken);
});

if (File.Exists(Path.Combine(app.Environment.WebRootPath ?? string.Empty, "index.html")))
{
    app.MapFallbackToFile("index.html");
}

app.Logger.LogInformation("Backend listening on {ServerUrl}", appOptions.ServerUrl);

app.Run();

static async Task WriteSseStreamAsync(
    HttpContext context,
    IAsyncEnumerable<BuildEventEnvelope> events,
    Guid? buildId,
    ILogger logger,
    CancellationToken cancellationToken)
{
    var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    serializerOptions.Converters.Add(new JsonStringEnumConverter());
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Append("X-Accel-Buffering", "no");
    context.Response.ContentType = "text/event-stream";
    var connectionId = context.TraceIdentifier;
    var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var eventCount = 0;

    logger.LogInformation(
        "SSE stream opened. ConnectionId={ConnectionId}, BuildId={BuildId}, RemoteIp={RemoteIp}, UserAgent={UserAgent}",
        connectionId,
        buildId,
        remoteIp,
        context.Request.Headers.UserAgent.ToString());

    try
    {
        await foreach (var envelope in events.WithCancellation(cancellationToken))
        {
            var payload = JsonSerializer.Serialize(envelope, serializerOptions);
            await context.Response.WriteAsync($"event: {envelope.EventType}\n", cancellationToken);
            await context.Response.WriteAsync($"data: {payload}\n\n", cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
            eventCount++;
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        logger.LogInformation(
            "SSE stream canceled by request abort. ConnectionId={ConnectionId}, BuildId={BuildId}, EventCount={EventCount}",
            connectionId,
            buildId,
            eventCount);
    }
    catch (IOException ex)
    {
        logger.LogInformation(
            ex,
            "SSE stream ended with IO exception. ConnectionId={ConnectionId}, BuildId={BuildId}, EventCount={EventCount}",
            connectionId,
            buildId,
            eventCount);
    }
    catch (Exception ex)
    {
        logger.LogWarning(
            ex,
            "SSE stream failed unexpectedly. ConnectionId={ConnectionId}, BuildId={BuildId}, EventCount={EventCount}",
            connectionId,
            buildId,
            eventCount);
        throw;
    }
    finally
    {
        logger.LogInformation(
            "SSE stream closed. ConnectionId={ConnectionId}, BuildId={BuildId}, EventCount={EventCount}",
            connectionId,
            buildId,
            eventCount);
    }
}

static string FlattenValidationErrors(Dictionary<string, string[]> errors)
{
    return string.Join(
        Environment.NewLine,
        errors.SelectMany(pair => pair.Value.Select(message => $"{pair.Key}: {message}")));
}
