using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Backend.Contracts;
using Backend.Data;
using Backend.Models;
using Backend.Options;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

public sealed class ProjectNotFoundException : Exception;

public sealed class BuildValidationException(Dictionary<string, string[]> errors) : Exception
{
    public Dictionary<string, string[]> Errors { get; } = errors;
}

public sealed record BuildEnqueueMetadata(BuildTriggerSource TriggerSource, Guid? ScheduleId = null);

public sealed class BuildOrchestrator(
    IDbContextFactory<BuildDbContext> dbFactory,
    StoragePaths storagePaths,
    AppOptions appOptions,
    BuildEventBroker eventBroker,
    AutomationToolJanitor automationToolJanitor,
    BuildLogAnalyzer logAnalyzer,
    ProjectValidator projectValidator,
    ILogger<BuildOrchestrator> logger) : BackgroundService
{
    private readonly Channel<bool> _dispatchSignals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest
    });

    private readonly ConcurrentDictionary<Guid, RunningBuildContext> _activeBuilds = new();
    private readonly ConcurrentDictionary<Guid, Guid> _activeProjects = new();
    private readonly int _maxConcurrency = Math.Max(1, appOptions.GlobalConcurrency);
    private readonly SemaphoreSlim _uatSlots = new(Math.Max(1, appOptions.UatConcurrency), Math.Max(1, appOptions.UatConcurrency));
    private readonly int _logBatchSize = Math.Max(1, appOptions.LogEventBatchSize);
    private readonly TimeSpan _logFlushInterval = TimeSpan.FromMilliseconds(Math.Max(100, appOptions.LogEventFlushMilliseconds));
    private readonly TimeSpan _progressPersistInterval = TimeSpan.FromMilliseconds(750);

    public Task<BuildRecord> EnqueueBuildAsync(QueueBuildRequest request, CancellationToken cancellationToken)
    {
        return EnqueueBuildAsync(request, null, cancellationToken);
    }

    public async Task<BuildRecord> EnqueueBuildAsync(
        QueueBuildRequest request,
        BuildEnqueueMetadata? metadata,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await automationToolJanitor.CleanupIfSystemIdleAsync("Build enqueue preflight", cancellationToken);
        var project = await db.Projects.FirstOrDefaultAsync(item => item.Id == request.ProjectId, cancellationToken);
        if (project is null)
        {
            throw new ProjectNotFoundException();
        }

        var errors = await projectValidator.ValidateBuildRequestAsync(project, request, cancellationToken);
        if (errors.Count > 0)
        {
            throw new BuildValidationException(errors);
        }

        var buildId = Guid.NewGuid();
        var buildRoot = storagePaths.ResolveBuildRoot(buildId);
        Directory.CreateDirectory(buildRoot);
        if (!string.IsNullOrWhiteSpace(project.ArchiveRootPath))
        {
            Directory.CreateDirectory(project.ArchiveRootPath);
        }

        var now = DateTimeOffset.UtcNow;
        var build = new BuildRecord
        {
            Id = buildId,
            ProjectId = project.Id,
            Project = project,
            Revision = BuildCommandFactory.NormalizeRevision(request.Revision),
            TriggerSource = metadata?.TriggerSource ?? BuildTriggerSource.Manual,
            ScheduleId = metadata?.ScheduleId,
            TargetType = request.TargetType,
            TargetName = BuildCommandFactory.ResolveTargetName(project, request.TargetType),
            BuildConfiguration = request.BuildConfiguration.Trim(),
            Clean = request.Clean,
            Pak = request.Pak,
            IoStore = request.IoStore,
            ExtraUatArgs = NormalizeExtraArgs(project.DefaultExtraUatArgs, request.ExtraUatArgs),
            Status = BuildStatus.Queued,
            CurrentPhase = BuildPhase.Queued,
            ProgressPercent = 0,
            StatusMessage = AppText.WaitingToRun,
            QueuedAtUtc = now,
            BuildRootPath = buildRoot,
            LogFilePath = storagePaths.ResolveLogPath(buildId),
            ArchiveDirectoryPath = string.Empty,
            ZipFilePath = string.Empty,
            DownloadUrl = null
        };

        var svnCommand = BuildCommandFactory.CreateSvnCommand(project, build);
        build.SvnCommandLine = svnCommand.DisplayString;
        build.UatCommandLine = BuildCommandFactory.CreateUatPreview(build);

        db.Builds.Add(build);
        await SqliteExecution.SaveChangesWithRetryAsync(db, logger, "enqueue build", cancellationToken);

        await RefreshQueuedBuildsAsync(cancellationToken);
        await eventBroker.PublishAsync(BuildEventEnvelopeForBuild(build, "build-status"));
        SignalDispatch();
        return build;
    }

    public async Task<BuildRecord?> CancelBuildAsync(Guid buildId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var build = await db.Builds.Include(item => item.Project).FirstOrDefaultAsync(item => item.Id == buildId, cancellationToken);
        if (build is null)
        {
            return null;
        }

        if (build.Status is BuildStatus.Succeeded or BuildStatus.Failed or BuildStatus.Interrupted)
        {
            return build;
        }

        if (build.Status == BuildStatus.Queued)
        {
            build.Status = BuildStatus.Interrupted;
            build.CurrentPhase = BuildPhase.Interrupted;
            build.StatusMessage = AppText.BuildCanceled;
            build.ErrorSummary = AppText.UserCanceledQueuedBuild;
            build.FinishedAtUtc = DateTimeOffset.UtcNow;
            await SqliteExecution.SaveChangesWithRetryAsync(db, logger, "cancel queued build", cancellationToken);
            await eventBroker.PublishAsync(BuildEventEnvelopeForBuild(build, "build-finished"));
            await RefreshQueuedBuildsAsync(cancellationToken);
            SignalDispatch();
            return build;
        }

        if (_activeBuilds.TryGetValue(build.Id, out var runtime))
        {
            runtime.Cancel(AppText.UserCanceledBuild);
        }

        return build;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverBuildQueueAsync(stoppingToken);
        SignalDispatch();

        while (!stoppingToken.IsCancellationRequested)
        {
            await DispatchQueuedBuildsAsync(stoppingToken);
            await _dispatchSignals.Reader.ReadAsync(stoppingToken);
        }
    }

    private async Task DispatchQueuedBuildsAsync(CancellationToken cancellationToken)
    {
        var startedAny = false;

        while (_activeBuilds.Count < _maxConcurrency && !cancellationToken.IsCancellationRequested)
        {
            var next = await FindNextRunnableBuildAsync(cancellationToken);
            if (next is null)
            {
                break;
            }

            if (!TryStartBuild(next.Value.BuildId, next.Value.ProjectId, cancellationToken))
            {
                continue;
            }

            startedAny = true;
        }

        if (startedAny)
        {
            await RefreshQueuedBuildsAsync(cancellationToken);
        }
    }

    private bool TryStartBuild(Guid buildId, Guid projectId, CancellationToken stoppingToken)
    {
        if (_activeBuilds.Count >= _maxConcurrency)
        {
            return false;
        }

        if (!_activeProjects.TryAdd(projectId, buildId))
        {
            return false;
        }

        var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var runtime = new RunningBuildContext(buildId, projectId, cancellationSource);
        if (!_activeBuilds.TryAdd(buildId, runtime))
        {
            _activeProjects.TryRemove(projectId, out _);
            cancellationSource.Dispose();
            return false;
        }

        runtime.ExecutionTask = Task.Run(() => ExecuteBuildInternalAsync(buildId, runtime, cancellationSource.Token), CancellationToken.None);
        _ = runtime.ExecutionTask.ContinueWith(
            _ => OnBuildCompleted(runtime),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return true;
    }

    private void OnBuildCompleted(RunningBuildContext runtime)
    {
        _activeBuilds.TryRemove(runtime.BuildId, out _);
        _activeProjects.TryRemove(runtime.ProjectId, out _);
        runtime.Dispose();
        _ = Task.Run(
            () => automationToolJanitor.CleanupIfSystemIdleAsync("Build completion cleanup", CancellationToken.None),
            CancellationToken.None);
        SignalDispatch();
    }

    private async Task<(Guid BuildId, Guid ProjectId)?> FindNextRunnableBuildAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var queuedBuilds = await db.Builds
            .AsNoTracking()
            .Where(build => build.Status == BuildStatus.Queued)
            .Select(build => new { build.Id, build.ProjectId, build.QueuedAtUtc })
            .ToListAsync(cancellationToken);

        queuedBuilds = queuedBuilds
            .OrderBy(build => build.QueuedAtUtc)
            .Take(200)
            .ToList();

        foreach (var build in queuedBuilds)
        {
            if (!_activeProjects.ContainsKey(build.ProjectId))
            {
                return (build.Id, build.ProjectId);
            }
        }

        return null;
    }

    private async Task ExecuteBuildInternalAsync(Guid buildId, RunningBuildContext runtime, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var build = await db.Builds.Include(item => item.Project).FirstOrDefaultAsync(item => item.Id == buildId, cancellationToken);
        if (build?.Project is null || build.Status != BuildStatus.Queued)
        {
            return;
        }

        var project = build.Project;
        Directory.CreateDirectory(build.BuildRootPath);

        build.Status = BuildStatus.Running;
        build.CurrentPhase = BuildPhase.SourceSync;
        build.ProgressPercent = 3;
        build.StartedAtUtc = DateTimeOffset.UtcNow;
        build.StatusMessage = AppText.SyncingSource;
        await SqliteExecution.SaveChangesWithRetryAsync(db, logger, "mark build started", cancellationToken);
        await eventBroker.PublishAsync(BuildEventEnvelopeForBuild(build, "build-status"));

        await using var logStream = new FileStream(build.LogFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        await using var writer = new StreamWriter(logStream, new UTF8Encoding(false)) { AutoFlush = true };

        try
        {
            await writer.WriteLineAsync($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] Build {build.Id} queued for project {project.Name}");
            await writer.WriteLineAsync(build.SvnCommandLine ?? string.Empty);
            await writer.WriteLineAsync(build.UatCommandLine ?? string.Empty);

            var progress = new ProgressTracker(build.CurrentPhase, build.ProgressPercent, build.StatusMessage);

            var svnExitCode = await RunProcessAsync(
                BuildCommandFactory.CreateSvnCommand(project, build),
                build,
                runtime,
                writer,
                progress,
                db,
                cancellationToken);

            if (svnExitCode != 0)
            {
                await MarkFailedAsync(build, db, AppText.SvnFailed(svnExitCode), svnExitCode, cancellationToken);
                return;
            }

            var resolvedRevision = await ResolveWorkingCopyRevisionAsync(project.WorkingCopyPath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(resolvedRevision))
            {
                build.Revision = resolvedRevision;
            }

            build.ArchiveDirectoryPath = storagePaths.ResolveArchiveDirectoryWithFallback(project, build, build.QueuedAtUtc);
            build.ZipFilePath = storagePaths.ResolveZipPath(build, build.QueuedAtUtc);
            if (string.IsNullOrWhiteSpace(project.ArchiveRootPath))
            {
                await writer.WriteLineAsync($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] ArchiveRootPath is empty. Falling back to local archive directory: {build.ArchiveDirectoryPath}");
                logger.LogWarning(
                    "Project {ProjectId} ({ProjectName}) has empty ArchiveRootPath. Falling back to build-local archive directory {ArchiveDirectoryPath}.",
                    project.Id,
                    project.Name,
                    build.ArchiveDirectoryPath);
            }
            build.UatCommandLine = BuildCommandFactory.CreateUatCommand(project, build).DisplayString;
            Directory.CreateDirectory(build.ArchiveDirectoryPath);
            await SqliteExecution.SaveChangesWithRetryAsync(db, logger, "store resolved revision", cancellationToken);
            await eventBroker.PublishAsync(BuildEventEnvelopeForBuild(build, "build-progress"));

            build.CurrentPhase = BuildPhase.Build;
            build.ProgressPercent = Math.Max(build.ProgressPercent, 12);
            build.StatusMessage = AppText.WaitingForUatSlot;
            await SqliteExecution.SaveChangesWithRetryAsync(db, logger, "wait for UAT slot", cancellationToken);
            await eventBroker.PublishAsync(BuildEventEnvelopeForBuild(build, "build-progress"));

            var uatSlotAcquired = false;
            try
            {
                await _uatSlots.WaitAsync(cancellationToken);
                uatSlotAcquired = true;
                build.CurrentPhase = BuildPhase.Build;
                build.ProgressPercent = Math.Max(build.ProgressPercent, 12);
                build.StatusMessage = AppText.RunningBuildCookRun;
                await SqliteExecution.SaveChangesWithRetryAsync(db, logger, "acquire UAT slot", cancellationToken);
                await eventBroker.PublishAsync(BuildEventEnvelopeForBuild(build, "build-progress"));

                var uatExitCode = await RunProcessAsync(
                    BuildCommandFactory.CreateUatCommand(project, build),
                    build,
                    runtime,
                    writer,
                    progress,
                    db,
                    cancellationToken);

                if (uatExitCode != 0)
                {
                    await MarkFailedAsync(build, db, null, uatExitCode, cancellationToken);
                    return;
                }
            }
            finally
            {
                if (uatSlotAcquired)
                {
                    _uatSlots.Release();
                }
            }

            build.CurrentPhase = BuildPhase.Zip;
            build.ProgressPercent = 96;
            build.StatusMessage = AppText.ZippingArtifacts;
            await SqliteExecution.SaveChangesWithRetryAsync(db, logger, "zip artifacts", cancellationToken);
            await eventBroker.PublishAsync(BuildEventEnvelopeForBuild(build, "build-progress"));

            if (!string.IsNullOrWhiteSpace(build.ZipFilePath))
            {
                var tempZipPath = $"{build.ZipFilePath}.partial";
                if (File.Exists(build.ZipFilePath))
                {
                    File.Delete(build.ZipFilePath);
                }

                if (File.Exists(tempZipPath))
                {
                    File.Delete(tempZipPath);
                }

                ZipFile.CreateFromDirectory(build.ArchiveDirectoryPath, tempZipPath);
                File.Move(tempZipPath, build.ZipFilePath, true);
            }

            build.Status = BuildStatus.Succeeded;
            build.CurrentPhase = BuildPhase.Completed;
            build.ProgressPercent = 100;
            build.StatusMessage = AppText.BuildCompleted;
            build.FinishedAtUtc = DateTimeOffset.UtcNow;
            build.ExitCode = 0;
            build.DownloadUrl = $"/api/builds/{build.Id}/download";
            await SqliteExecution.SaveChangesWithRetryAsync(db, logger, "mark build succeeded", cancellationToken);
            await eventBroker.PublishAsync(BuildEventEnvelopeForBuild(build, "build-finished"));
        }
        catch (OperationCanceledException)
        {
            build.Status = BuildStatus.Interrupted;
            build.CurrentPhase = BuildPhase.Interrupted;
            build.ProgressPercent = Math.Max(build.ProgressPercent, 1);
            build.StatusMessage = runtime.CancellationReason == AppText.UserCanceledBuild
                ? AppText.BuildCanceled
                : AppText.BuildInterrupted;
            build.ErrorSummary = runtime.CancellationReason ?? AppText.ServiceStoppingInterrupted;
            build.FinishedAtUtc = DateTimeOffset.UtcNow;
            build.DownloadUrl = null;
            await SqliteExecution.SaveChangesWithRetryAsync(db, logger, "mark build interrupted", CancellationToken.None);
            await eventBroker.PublishAsync(BuildEventEnvelopeForBuild(build, "build-finished"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Build {BuildId} crashed unexpectedly.", build.Id);
            await writer.WriteLineAsync(ex.ToString());
            build.Status = BuildStatus.Failed;
            build.CurrentPhase = BuildPhase.Failed;
            build.StatusMessage = AppText.UnexpectedExecutorFailure;
            build.ErrorSummary = ex.Message;
            build.FinishedAtUtc = DateTimeOffset.UtcNow;
            build.DownloadUrl = null;
            await SqliteExecution.SaveChangesWithRetryAsync(db, logger, "mark build crashed", CancellationToken.None);
            await eventBroker.PublishAsync(BuildEventEnvelopeForBuild(build, "build-finished"));
        }
    }

    private async Task<int> RunProcessAsync(
        ProcessCommand command,
        BuildRecord build,
        RunningBuildContext runtime,
        StreamWriter writer,
        ProgressTracker progress,
        BuildDbContext db,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(command.FileName)
        {
            WorkingDirectory = command.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        await automationToolJanitor.TrackOwnedProcessAsync(build.Id, command, process.Id, cancellationToken);
        runtime.AttachProcess(process);
        using var cancellationRegistration = cancellationToken.Register(() => TryKillProcess(process));

        var lineChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        var stdoutTask = PumpReaderAsync(process.StandardOutput, lineChannel.Writer, cancellationToken);
        var stderrTask = PumpReaderAsync(process.StandardError, lineChannel.Writer, cancellationToken);
        var completionTask = Task.WhenAll(stdoutTask, stderrTask)
            .ContinueWith(_ => lineChannel.Writer.TryComplete(), CancellationToken.None);

        var pendingLines = new List<string>(_logBatchSize);
        var nextFlushAt = DateTime.UtcNow.Add(_logFlushInterval);
        var lastProgressPersistAt = DateTime.MinValue;

        await foreach (var line in lineChannel.Reader.ReadAllAsync(cancellationToken))
        {
            await writer.WriteLineAsync(line);
            build.LogLineCount++;
            pendingLines.Add(line);

            if (TryAdvanceProgress(line, progress, out var newPhase, out var newPercent, out var message))
            {
                var phaseChanged = build.CurrentPhase != newPhase;
                var messageChanged = !string.Equals(build.StatusMessage, message, StringComparison.Ordinal);
                var percentChanged = build.ProgressPercent != Math.Max(build.ProgressPercent, newPercent);

                build.CurrentPhase = newPhase;
                build.ProgressPercent = Math.Max(build.ProgressPercent, newPercent);
                build.StatusMessage = message;
                var shouldPersistProgress = phaseChanged ||
                                            messageChanged ||
                                            (percentChanged && DateTime.UtcNow - lastProgressPersistAt >= _progressPersistInterval);

                if (shouldPersistProgress)
                {
                    await SqliteExecution.SaveChangesWithRetryAsync(db, logger, "persist build progress", cancellationToken);
                    lastProgressPersistAt = DateTime.UtcNow;
                    await eventBroker.PublishAsync(BuildEventEnvelopeForBuild(build, "build-progress"));
                }
            }

            var shouldFlush = pendingLines.Count >= _logBatchSize || DateTime.UtcNow >= nextFlushAt;
            if (shouldFlush)
            {
                await PublishLogChunkAsync(build, pendingLines, cancellationToken);
                pendingLines.Clear();
                nextFlushAt = DateTime.UtcNow.Add(_logFlushInterval);
            }
        }

        if (pendingLines.Count > 0)
        {
            await PublishLogChunkAsync(build, pendingLines, cancellationToken);
        }

        try
        {
            await completionTask;
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode;
        }
        finally
        {
            runtime.DetachProcess();
            await automationToolJanitor.ClearOwnedProcessAsync(build.Id, CancellationToken.None);
        }
    }

    private static async Task<string?> ResolveWorkingCopyRevisionAsync(string workingCopyPath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("svn")
        {
            WorkingDirectory = workingCopyPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.ArgumentList.Add("info");
        startInfo.ArgumentList.Add("--show-item");
        startInfo.ArgumentList.Add("revision");

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var stdout = (await stdoutTask).Trim();
        _ = await stderrTask;

        return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout) ? stdout : null;
    }

    private async Task PublishLogChunkAsync(BuildRecord build, IReadOnlyList<string> lines, CancellationToken cancellationToken)
    {
        await eventBroker.PublishAsync(new BuildEventEnvelope(
            "build-log",
            build.Id,
            new
            {
                buildId = build.Id,
                lines,
                totalLines = build.LogLineCount
            },
            DateTimeOffset.UtcNow));
    }

    private async Task MarkFailedAsync(
        BuildRecord build,
        BuildDbContext db,
        string? fallbackSummary,
        int exitCode,
        CancellationToken cancellationToken)
    {
        build.Status = BuildStatus.Failed;
        build.CurrentPhase = BuildPhase.Failed;
        build.StatusMessage = AppText.BuildFailed;
        build.ExitCode = exitCode;
        build.FinishedAtUtc = DateTimeOffset.UtcNow;
        build.DownloadUrl = null;
        build.ErrorSummary = fallbackSummary ?? logAnalyzer.ExtractSummary(build.LogFilePath, exitCode);
        if (!string.IsNullOrWhiteSpace(build.ErrorSummary) &&
            build.ErrorSummary.Contains("conflicting instance of AutomationTool", StringComparison.OrdinalIgnoreCase))
        {
            build.ErrorSummary = AppText.UatSingleInstanceConflict;
        }
        await SqliteExecution.SaveChangesWithRetryAsync(db, logger, "mark build failed", cancellationToken);
        await eventBroker.PublishAsync(BuildEventEnvelopeForBuild(build, "build-finished"));
    }

    private async Task RecoverBuildQueueAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var runningBuilds = await db.Builds
            .Where(build => build.Status == BuildStatus.Running)
            .ToListAsync(cancellationToken);

        foreach (var build in runningBuilds)
        {
            build.Status = BuildStatus.Interrupted;
            build.CurrentPhase = BuildPhase.Interrupted;
            build.StatusMessage = AppText.BuildInterrupted;
            build.ErrorSummary ??= AppText.ServiceRestartInterrupted;
            build.FinishedAtUtc = DateTimeOffset.UtcNow;
        }

        await SqliteExecution.SaveChangesWithRetryAsync(db, logger, "recover interrupted builds", cancellationToken);
        await RefreshQueuedBuildsAsync(cancellationToken);
        await automationToolJanitor.CleanupIfSystemIdleAsync("Service startup recovery", cancellationToken);
    }

    private async Task RefreshQueuedBuildsAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var queuedBuilds = await db.Builds
            .Include(build => build.Project)
            .Where(build => build.Status == BuildStatus.Queued)
            .ToListAsync(cancellationToken);

        queuedBuilds = queuedBuilds
            .OrderBy(build => build.QueuedAtUtc)
            .ToList();

        var changed = false;
        for (var index = 0; index < queuedBuilds.Count; index++)
        {
            var build = queuedBuilds[index];
            var nextMessage = AppText.QueueWaiting(index + 1);
            if (build.CurrentPhase != BuildPhase.Queued || build.ProgressPercent != 0 || build.StatusMessage != nextMessage)
            {
                build.CurrentPhase = BuildPhase.Queued;
                build.ProgressPercent = 0;
                build.StatusMessage = nextMessage;
                changed = true;
            }
        }

        if (changed)
        {
            await SqliteExecution.SaveChangesWithRetryAsync(db, logger, "refresh queued builds", cancellationToken);
        }

        foreach (var build in queuedBuilds)
        {
            await eventBroker.PublishAsync(BuildEventEnvelopeForBuild(build, "build-status"));
        }
    }

    private void SignalDispatch()
    {
        _dispatchSignals.Writer.TryWrite(true);
    }

    private static List<string> NormalizeExtraArgs(IEnumerable<string> defaults, IEnumerable<string>? extra)
    {
        return defaults
            .Concat(extra ?? Array.Empty<string>())
            .Select(item => item.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task PumpReaderAsync(StreamReader reader, ChannelWriter<string> writer, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            await writer.WriteAsync(line, cancellationToken);
        }
    }

    private static bool TryAdvanceProgress(string line, ProgressTracker tracker, out BuildPhase phase, out int percent, out string message)
    {
        phase = tracker.CurrentPhase;
        percent = tracker.CurrentPercent;
        message = tracker.Message;

        var nextPhase = tracker.CurrentPhase;
        var nextPercent = tracker.CurrentPercent;
        var nextMessage = tracker.Message;
        var trimmedLine = line.Trim();

        if (trimmedLine.Length == 0 || BuildRegex.CommandPreview.IsMatch(trimmedLine))
        {
            return false;
        }

        if (tracker.CurrentPhase == BuildPhase.SourceSync)
        {
            nextMessage = AppText.SyncingSource;
            nextPercent = Math.Min(9, tracker.CurrentPercent + 1);
        }

        if (BuildRegex.BuildCompileProgress.TryMatch(trimmedLine, out var buildCurrent, out var buildTotal))
        {
            nextPhase = BuildPhase.Build;
            nextMessage = $"正在编译 ({buildCurrent}/{buildTotal})";
            nextPercent = Math.Max(nextPercent, ProgressTracker.MapProgress(BuildPhase.Build, buildCurrent, buildTotal));

            var compileChanged = nextPhase != tracker.CurrentPhase || nextPercent != tracker.CurrentPercent || nextMessage != tracker.Message;
            if (!compileChanged)
            {
                return false;
            }

            tracker.Update(nextPhase, nextPercent, nextMessage);
            phase = nextPhase;
            percent = nextPercent;
            message = nextMessage;
            return true;
        }

        if (BuildRegex.CookProgress.TryMatch(trimmedLine, out var cookCurrent, out var cookTotal))
        {
            nextPhase = BuildPhase.Cook;
            nextMessage = $"正在 Cook ({cookCurrent}/{cookTotal})";
            nextPercent = Math.Max(nextPercent, ProgressTracker.MapProgress(BuildPhase.Cook, cookCurrent, cookTotal));

            var cookChanged = nextPhase != tracker.CurrentPhase || nextPercent != tracker.CurrentPercent || nextMessage != tracker.Message;
            if (!cookChanged)
            {
                return false;
            }

            tracker.Update(nextPhase, nextPercent, nextMessage);
            phase = nextPhase;
            percent = nextPercent;
            message = nextMessage;
            return true;
        }

        if (BuildRegex.BuildStart.IsMatch(line))
        {
            nextPhase = BuildPhase.Build;
            nextMessage = "正在编译";
            nextPercent = Math.Max(nextPercent, 18);
        }
        else if (BuildRegex.CookStart.IsMatch(line))
        {
            nextPhase = BuildPhase.Cook;
            nextMessage = "正在 Cook";
            nextPercent = Math.Max(nextPercent, 48);
        }
        else if (BuildRegex.StageStart.IsMatch(line))
        {
            nextPhase = BuildPhase.Stage;
            nextMessage = "正在 Stage";
            nextPercent = Math.Max(nextPercent, 72);
        }
        else if (BuildRegex.PackageStart.IsMatch(line))
        {
            nextPhase = BuildPhase.Package;
            nextMessage = "正在 Package";
            nextPercent = Math.Max(nextPercent, 86);
        }
        else if (BuildRegex.ArchiveStart.IsMatch(line))
        {
            nextPhase = BuildPhase.Archive;
            nextMessage = "正在归档";
            nextPercent = Math.Max(nextPercent, 92);
        }
        else
        {
            nextPercent = tracker.NextIncrementalPercent();
        }

        var changed = nextPhase != tracker.CurrentPhase || nextPercent != tracker.CurrentPercent || nextMessage != tracker.Message;
        if (!changed)
        {
            return false;
        }

        tracker.Update(nextPhase, nextPercent, nextMessage);
        phase = nextPhase;
        percent = nextPercent;
        message = nextMessage;
        return true;
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch
        {
            // Ignore kill failures. The cancellation path will still mark the build interrupted.
        }
    }

    private static BuildEventEnvelope BuildEventEnvelopeForBuild(BuildRecord build, string eventType)
    {
        return new BuildEventEnvelope(
            eventType,
            build.Id,
            new
            {
                buildId = build.Id,
                projectId = build.ProjectId,
                triggerSource = build.TriggerSource,
                scheduleId = build.ScheduleId,
                status = build.Status,
                phase = build.CurrentPhase,
                progressPercent = build.ProgressPercent,
                statusMessage = build.StatusMessage,
                queuedAtUtc = build.QueuedAtUtc,
                startedAtUtc = build.StartedAtUtc,
                finishedAtUtc = build.FinishedAtUtc,
                errorSummary = build.ErrorSummary,
                exitCode = build.ExitCode,
                downloadUrl = build.DownloadUrl
            },
            DateTimeOffset.UtcNow);
    }

    private sealed class RunningBuildContext(Guid buildId, Guid projectId, CancellationTokenSource cancellationSource) : IDisposable
    {
        private readonly object _sync = new();
        private Process? _process;

        public Guid BuildId { get; } = buildId;

        public Guid ProjectId { get; } = projectId;

        public CancellationTokenSource CancellationSource { get; } = cancellationSource;

        public Task? ExecutionTask { get; set; }

        public string? CancellationReason { get; private set; }

        public void AttachProcess(Process process)
        {
            lock (_sync)
            {
                _process = process;
            }
        }

        public void DetachProcess()
        {
            lock (_sync)
            {
                _process = null;
            }
        }

        public void Cancel(string reason)
        {
            CancellationReason = reason;
            CancellationSource.Cancel();

            lock (_sync)
            {
                if (_process is not null)
                {
                    TryKillProcess(_process);
                }
            }
        }

        public void Dispose()
        {
            CancellationSource.Dispose();
        }
    }

    private sealed class ProgressTracker(BuildPhase currentPhase, int currentPercent, string message)
    {
        private int _phaseLineCount;

        public BuildPhase CurrentPhase { get; private set; } = currentPhase;

        public int CurrentPercent { get; private set; } = currentPercent;

        public string Message { get; private set; } = message;

        public void Update(BuildPhase phase, int percent, string nextMessage)
        {
            if (phase != CurrentPhase)
            {
                _phaseLineCount = 0;
            }
            else
            {
                _phaseLineCount++;
            }

            CurrentPhase = phase;
            CurrentPercent = percent;
            Message = nextMessage;
        }

        public int NextIncrementalPercent()
        {
            _phaseLineCount++;
            return CurrentPhase switch
            {
                BuildPhase.SourceSync => Math.Min(9, CurrentPercent + (_phaseLineCount % 5 == 0 ? 1 : 0)),
                BuildPhase.Build => Math.Min(47, CurrentPercent + (_phaseLineCount % 12 == 0 ? 1 : 0)),
                BuildPhase.Cook => Math.Min(69, CurrentPercent + (_phaseLineCount % 8 == 0 ? 1 : 0)),
                BuildPhase.Stage => Math.Min(84, CurrentPercent + (_phaseLineCount % 5 == 0 ? 1 : 0)),
                BuildPhase.Package => Math.Min(91, CurrentPercent + (_phaseLineCount % 4 == 0 ? 1 : 0)),
                BuildPhase.Archive => Math.Min(95, CurrentPercent + (_phaseLineCount % 2 == 0 ? 1 : 0)),
                _ => CurrentPercent
            };
        }

        public static int MapProgress(BuildPhase phase, int current, int total)
        {
            if (total <= 0)
            {
                return phase switch
                {
                    BuildPhase.Build => 18,
                    BuildPhase.Cook => 48,
                    _ => 0
                };
            }

            var boundedCurrent = Math.Clamp(current, 0, total);
            var ratio = (double)boundedCurrent / total;

            return phase switch
            {
                BuildPhase.Build => 18 + (int)Math.Round(ratio * 29),
                BuildPhase.Cook => 48 + (int)Math.Round(ratio * 21),
                _ => 0
            };
        }
    }

    private static class BuildRegex
    {
        public static readonly Regex BuildStart = new("(BUILD COMMAND STARTED|Running: .*UnrealBuildTool|Building .+\\.target)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        public static readonly Regex CookStart = new("(COOK COMMAND STARTED| Cook:|Cooking |Cooked packages)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        public static readonly Regex StageStart = new("(STAGE COMMAND STARTED|Creating Staging Manifest|Copying NonUFS)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        public static readonly Regex PackageStart = new("(PACKAGE COMMAND STARTED|Running: .*UnrealPak|Running: .*IoStore|Executing.*UnrealPak|Executing.*IoStore|PackagingResults)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        public static readonly Regex ArchiveStart = new("(ARCHIVE COMMAND STARTED|Archiving)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        public static readonly Regex CommandPreview = new("^(svn update -r |cmd\\.exe /c .+RunUAT\\.bat )", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        public static readonly ProgressRegex BuildCompileProgress = new(@"\[(\d+)\/(\d+)\]");
        public static readonly ProgressRegex CookProgress = new(@"Cooked packages.*?(\d+)\D+(\d+)", true);
    }

    private sealed class ProgressRegex(string pattern, bool remainingThenTotal = false)
    {
        private readonly Regex _regex = new(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private readonly bool _remainingThenTotal = remainingThenTotal;

        public bool TryMatch(string input, out int current, out int total)
        {
            current = 0;
            total = 0;

            var match = _regex.Match(input);
            if (!match.Success || match.Groups.Count < 3)
            {
                return false;
            }

            if (!int.TryParse(match.Groups[1].Value, out var first) || !int.TryParse(match.Groups[2].Value, out var second))
            {
                return false;
            }

            if (_remainingThenTotal)
            {
                total = second;
                current = Math.Clamp(total - first, 0, total);
                return total > 0;
            }

            current = first;
            total = second;
            return total > 0;
        }
    }
}
