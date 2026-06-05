using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using Backend.Contracts;
using Backend.Models;

namespace Backend.Services;

public sealed class BuildStageLogService(StoragePaths storagePaths, BuildLogReader logReader)
{
    private static readonly Regex LegacyIoStoreStart = new("(Running: .*IoStore|Executing.*IoStore)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LegacyUnrealPakIoStoreStart = new("(Running: .*UnrealPak|Executing.*UnrealPak).*(CreateGlobalContainer|PackageStoreManifest|IoStoreCommands)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    static BuildStageLogService()
    {
        ManifestJsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public BuildStageLogCaptureSession CreateCaptureSession(BuildRecord build, ProjectConfig project)
    {
        var stageLogsRootPath = storagePaths.ResolveStageLogsRoot(build.BuildRootPath);
        var manifestPath = storagePaths.ResolveStageLogManifestPath(build.BuildRootPath);
        var uatLogRootPath = Path.Combine(
            project.EngineRootPath,
            "Engine",
            "Programs",
            "AutomationTool",
            "Saved",
            "Logs");

        return new BuildStageLogCaptureSession(stageLogsRootPath, manifestPath, uatLogRootPath, build.IoStore);
    }

    public async Task<BuildStageLogListDto> ReadListAsync(BuildRecord build, CancellationToken cancellationToken)
    {
        var manifest = await ReadManifestAsync(build.BuildRootPath, cancellationToken);
        return ToListDto(build.Id, FilterManifest(build, manifest));
    }

    public async Task<BuildStageLogSnapshotDto?> ReadStageLogAsync(
        BuildRecord build,
        string stageKey,
        int tailLines,
        CancellationToken cancellationToken)
    {
        var manifest = await ReadManifestAsync(build.BuildRootPath, cancellationToken);
        var entry = FilterManifest(build, manifest).Stages.FirstOrDefault(item => string.Equals(item.StageKey, stageKey, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return null;
        }

        var logPath = ResolveStageFilePath(build.BuildRootPath, entry.LogRelativePath);
        var snapshot = await logReader.ReadAsync(logPath, tailLines, entry.LineCount, cancellationToken);
        return new BuildStageLogSnapshotDto(
            ToSummaryDto(build.Id, entry),
            snapshot.Lines,
            snapshot.IncludedLines,
            snapshot.TotalLines,
            snapshot.Truncated);
    }

    public async Task<(BuildStageLogSummaryDto Stage, string FilePath)?> ResolveStageLogDownloadAsync(
        BuildRecord build,
        string stageKey,
        CancellationToken cancellationToken)
    {
        var manifest = await ReadManifestAsync(build.BuildRootPath, cancellationToken);
        var entry = FilterManifest(build, manifest).Stages.FirstOrDefault(item => string.Equals(item.StageKey, stageKey, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return null;
        }

        var filePath = ResolveStageFilePath(build.BuildRootPath, entry.LogRelativePath);
        return !File.Exists(filePath)
            ? null
            : (ToSummaryDto(build.Id, entry), filePath);
    }

    public async Task<(BuildStageArtifactDto Artifact, string FilePath)?> ResolveArtifactDownloadAsync(
        BuildRecord build,
        string stageKey,
        string artifactKey,
        CancellationToken cancellationToken)
    {
        var manifest = await ReadManifestAsync(build.BuildRootPath, cancellationToken);
        var entry = FilterManifest(build, manifest).Stages.FirstOrDefault(item => string.Equals(item.StageKey, stageKey, StringComparison.OrdinalIgnoreCase));
        var artifact = entry?.InputArtifacts.FirstOrDefault(item => string.Equals(item.ArtifactKey, artifactKey, StringComparison.OrdinalIgnoreCase));
        if (entry is null || artifact is null)
        {
            return null;
        }

        var filePath = ResolveStageFilePath(build.BuildRootPath, artifact.RelativePath);
        return !File.Exists(filePath)
            ? null
            : (ToArtifactDto(build.Id, entry.StageKey, artifact), filePath);
    }

    public bool HasStageLogs(BuildRecord build)
    {
        var stageLogsRootPath = storagePaths.ResolveStageLogsRoot(build.BuildRootPath);
        return Directory.Exists(stageLogsRootPath) &&
               File.Exists(storagePaths.ResolveStageLogManifestPath(build.BuildRootPath));
    }

    public async Task WriteArchiveToAsync(BuildRecord build, Stream target, CancellationToken cancellationToken)
    {
        var stageLogsRootPath = storagePaths.ResolveStageLogsRoot(build.BuildRootPath);
        using var archive = new ZipArchive(target, ZipArchiveMode.Create, leaveOpen: true);
        if (!Directory.Exists(stageLogsRootPath))
        {
            return;
        }

        foreach (var filePath in Directory.EnumerateFiles(stageLogsRootPath, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(stageLogsRootPath, filePath).Replace('\\', '/');
            var entry = archive.CreateEntry($"stage-logs/{relativePath}", CompressionLevel.Fastest);
            await using var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await using var output = entry.Open();
            await input.CopyToAsync(output, cancellationToken);
        }
    }

    public async Task MarkDanglingStagesAsync(
        string buildRootPath,
        BuildStageLogStatus status,
        CancellationToken cancellationToken)
    {
        var manifest = await ReadManifestAsync(buildRootPath, cancellationToken);
        var changed = false;
        foreach (var stage in manifest.Stages.Where(item => item.Status == BuildStageLogStatus.Running))
        {
            stage.Status = status;
            stage.FinishedAtUtc ??= DateTimeOffset.UtcNow;
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        var manifestPath = storagePaths.ResolveStageLogManifestPath(buildRootPath);
        var tempPath = $"{manifestPath}.tmp";
        var json = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
        await File.WriteAllTextAsync(tempPath, json, new UTF8Encoding(false), cancellationToken);
        File.Move(tempPath, manifestPath, true);
    }

    public BuildStageLogListDto ToListDto(Guid buildId, BuildStageLogManifest manifest)
    {
        return new BuildStageLogListDto(
            manifest.Stages
                .OrderBy(item => item.Order)
                .Select(item => ToSummaryDto(buildId, item))
                .ToList());
    }

    public static string BuildArchiveDownloadFileName(BuildRecord build)
    {
        return $"stage-logs-{build.Id:N}.zip";
    }

    private async Task<BuildStageLogManifest> ReadManifestAsync(string buildRootPath, CancellationToken cancellationToken)
    {
        var manifestPath = storagePaths.ResolveStageLogManifestPath(buildRootPath);
        if (!File.Exists(manifestPath))
        {
            return new BuildStageLogManifest();
        }

        try
        {
            await using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var json = await reader.ReadToEndAsync(cancellationToken);
            return JsonSerializer.Deserialize<BuildStageLogManifest>(json, ManifestJsonOptions) ?? new BuildStageLogManifest();
        }
        catch
        {
            return new BuildStageLogManifest();
        }
    }

    private BuildStageLogSummaryDto ToSummaryDto(Guid buildId, BuildStageLogManifestEntry entry)
    {
        return new BuildStageLogSummaryDto(
            entry.StageKey,
            entry.Kind,
            entry.DisplayName,
            entry.ParentStageKey,
            entry.Order,
            entry.Status,
            entry.StartedAtUtc,
            entry.FinishedAtUtc,
            entry.LineCount,
            $"/api/builds/{buildId}/stage-logs/{entry.StageKey}/download",
            entry.InputArtifacts.Select(item => ToArtifactDto(buildId, entry.StageKey, item)).ToList());
    }

    private static BuildStageArtifactDto ToArtifactDto(Guid buildId, string stageKey, BuildStageLogArtifactEntry entry)
    {
        return new BuildStageArtifactDto(
            entry.ArtifactKey,
            entry.Label,
            entry.Category,
            entry.FileName,
            entry.SizeBytes,
            $"/api/builds/{buildId}/stage-logs/{stageKey}/artifacts/{entry.ArtifactKey}/download");
    }

    private string ResolveStageFilePath(string buildRootPath, string relativePath)
    {
        return Path.Combine(storagePaths.ResolveStageLogsRoot(buildRootPath), relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private BuildStageLogManifest FilterManifest(BuildRecord build, BuildStageLogManifest manifest)
    {
        return new BuildStageLogManifest
        {
            Stages = manifest.Stages
                .Where(entry => ShouldExposeStage(build, entry))
                .ToList()
        };
    }

    private bool ShouldExposeStage(BuildRecord build, BuildStageLogManifestEntry entry)
    {
        if (entry.Kind != BuildStageLogKind.IoStore)
        {
            return true;
        }

        if (!build.IoStore)
        {
            return false;
        }

        if (!string.Equals(entry.ParentStageKey, "package", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return HasValidIoStoreStart(build.BuildRootPath, entry);
    }

    private bool HasValidIoStoreStart(string buildRootPath, BuildStageLogManifestEntry entry)
    {
        var logPath = ResolveStageFilePath(buildRootPath, entry.LogRelativePath);
        if (!TryReadFirstNonEmptyLine(logPath, out var line))
        {
            return true;
        }

        return LegacyIoStoreStart.IsMatch(line) || LegacyUnrealPakIoStoreStart.IsMatch(line);
    }

    private static bool TryReadFirstNonEmptyLine(string path, out string line)
    {
        line = string.Empty;
        if (!File.Exists(path))
        {
            return false;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (!reader.EndOfStream)
        {
            var next = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(next))
            {
                continue;
            }

            line = next.Trim();
            return true;
        }

        return false;
    }

    public sealed class BuildStageLogCaptureSession(
        string stageLogsRootPath,
        string manifestPath,
        string uatLogRootPath,
        bool ioStoreEnabled) : IAsyncDisposable
    {
        private static readonly Dictionary<BuildPhase, int> PhaseOrder = new()
        {
            [BuildPhase.SourceSync] = 1,
            [BuildPhase.Build] = 2,
            [BuildPhase.Cook] = 3,
            [BuildPhase.Stage] = 4,
            [BuildPhase.Package] = 5,
            [BuildPhase.Archive] = 6,
            [BuildPhase.Zip] = 7
        };
        private static readonly SemaphoreSlim ManifestWriteLock = new(1, 1);
        private static readonly Regex ToolLogLine = new(@"^Log file:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex UbtStart = new("(Running: .*UnrealBuildTool|Building .+\\.target)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex PakStart = new("(Running: .*UnrealPak|Executing.*UnrealPak)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex IoStoreStart = new("(Running: .*IoStore|Executing.*IoStore)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex UnrealPakIoStoreStart = new("(Running: .*UnrealPak|Executing.*UnrealPak).*(CreateGlobalContainer|PackageStoreManifest|IoStoreCommands)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex FinalCopyArtifacts = new("^FinalCopy", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex PakArtifacts = new("^(PakCommands|PrePak_)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex IoStoreArtifacts = new("^IoStoreCommands", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly BuildStageLogManifest _manifest = new();
        private readonly Dictionary<string, ActiveStageContext> _activeStages = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<BuildStageLogKind, int> _kindCounts = new();
        private int _nextOrder = 1;
        private bool _dirty = true;
        private bool _stateChanged;
        private BuildPhase? _furthestPrimaryPhase;

        public string? CurrentPrimaryStageKey { get; private set; }

        public string? CurrentSubStageKey { get; private set; }

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(stageLogsRootPath);
            await PersistManifestAsync(cancellationToken);
        }

        public async Task StartStandaloneStageAsync(
            BuildStageLogKind kind,
            ProcessCommand command,
            CancellationToken cancellationToken)
        {
            await TransitionPrimaryStageAsync(kind, command, cancellationToken);
        }

        public async Task HandleUatLineAsync(
            string line,
            BuildPhase currentPhase,
            ProcessCommand command,
            CancellationToken cancellationToken)
        {
            currentPhase = NormalizePrimaryPhase(currentPhase);
            var primaryKind = MapPrimaryKind(currentPhase);
            await TransitionPrimaryStageAsync(primaryKind, command, cancellationToken);

            var subStageSignal = DetectSubStage(line, primaryKind);
            if (subStageSignal.Kind.HasValue)
            {
                await TransitionSubStageAsync(subStageSignal.Kind.Value, command, subStageSignal.StartMarker, cancellationToken);
            }

            if (CurrentSubStageKey is not null &&
                _activeStages.TryGetValue(CurrentSubStageKey, out var currentSubStage) &&
                currentSubStage.Entry.Kind == BuildStageLogKind.UBT &&
                TryGetToolLogPath(line, out var toolLogPath))
            {
                currentSubStage.PendingToolLogPaths.Add(toolLogPath);
            }

            await AppendLineAsync(line, cancellationToken);
        }

        public async Task WriteStandaloneLineAsync(string line, CancellationToken cancellationToken)
        {
            await AppendLineAsync(line, cancellationToken);
        }

        public async Task CompleteActiveStagesAsync(BuildStageLogStatus status, CancellationToken cancellationToken)
        {
            if (CurrentSubStageKey is not null)
            {
                await CloseStageAsync(CurrentSubStageKey, status, cancellationToken);
                CurrentSubStageKey = null;
            }

            if (CurrentPrimaryStageKey is not null)
            {
                await CloseStageAsync(CurrentPrimaryStageKey, status, cancellationToken);
                CurrentPrimaryStageKey = null;
            }
        }

        public async Task FlushAsync(CancellationToken cancellationToken)
        {
            foreach (var context in _activeStages.Values)
            {
                await context.Writer.FlushAsync(cancellationToken);
            }

            await PersistManifestAsync(cancellationToken);
        }

        public bool TryConsumeStateChanged()
        {
            if (!_stateChanged)
            {
                return false;
            }

            _stateChanged = false;
            return true;
        }

        public BuildStageLogListDto ToListDto(Guid buildId)
        {
            return new BuildStageLogListDto(
                _manifest.Stages
                    .OrderBy(item => item.Order)
                    .Select(item => new BuildStageLogSummaryDto(
                        item.StageKey,
                        item.Kind,
                        item.DisplayName,
                        item.ParentStageKey,
                        item.Order,
                        item.Status,
                        item.StartedAtUtc,
                        item.FinishedAtUtc,
                        item.LineCount,
                        $"/api/builds/{buildId}/stage-logs/{item.StageKey}/download",
                        item.InputArtifacts.Select(artifact => new BuildStageArtifactDto(
                            artifact.ArtifactKey,
                            artifact.Label,
                            artifact.Category,
                            artifact.FileName,
                            artifact.SizeBytes,
                            $"/api/builds/{buildId}/stage-logs/{item.StageKey}/artifacts/{artifact.ArtifactKey}/download"))
                            .ToList()))
                    .ToList());
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var context in _activeStages.Values)
            {
                await context.Writer.DisposeAsync();
                await context.Stream.DisposeAsync();
            }

            _activeStages.Clear();
        }

        private async Task TransitionPrimaryStageAsync(
            BuildStageLogKind kind,
            ProcessCommand command,
            CancellationToken cancellationToken)
        {
            var desiredKey = BuildPrimaryStageKey(kind);
            if (string.Equals(CurrentPrimaryStageKey, desiredKey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (CurrentSubStageKey is not null)
            {
                await CloseStageAsync(CurrentSubStageKey, BuildStageLogStatus.Completed, cancellationToken);
                CurrentSubStageKey = null;
            }

            if (CurrentPrimaryStageKey is not null)
            {
                await CloseStageAsync(CurrentPrimaryStageKey, BuildStageLogStatus.Completed, cancellationToken);
            }

            var context = await OpenStageAsync(
                stageKey: desiredKey,
                kind,
                GetPrimaryStageDisplayName(kind),
                parentStageKey: null,
                command,
                startMarker: command.DisplayString,
                cancellationToken);

            CurrentPrimaryStageKey = context.Entry.StageKey;
        }

        private async Task TransitionSubStageAsync(
            BuildStageLogKind kind,
            ProcessCommand command,
            string startMarker,
            CancellationToken cancellationToken)
        {
            if (CurrentPrimaryStageKey is null)
            {
                return;
            }

            if (CurrentSubStageKey is not null &&
                _activeStages.TryGetValue(CurrentSubStageKey, out var activeSubStage))
            {
                if (activeSubStage.Entry.Kind == kind &&
                    string.Equals(activeSubStage.StartMarker, startMarker, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                await CloseStageAsync(CurrentSubStageKey, BuildStageLogStatus.Completed, cancellationToken);
                CurrentSubStageKey = null;
            }

            var index = NextKindIndex(kind);
            var stageKey = $"{kind.ToString().ToLowerInvariant()}-{index:00}";
            var displayName = index == 1 ? kind.ToString() : $"{kind} #{index}";
            var displayString = startMarker.StartsWith("Running:", StringComparison.OrdinalIgnoreCase) ||
                                startMarker.StartsWith("Executing", StringComparison.OrdinalIgnoreCase)
                ? startMarker
                : command.DisplayString;

            var context = await OpenStageAsync(
                stageKey,
                kind,
                displayName,
                CurrentPrimaryStageKey,
                CloneWithDisplay(command, displayString),
                startMarker,
                cancellationToken);

            CurrentSubStageKey = context.Entry.StageKey;
        }

        private async Task AppendLineAsync(string line, CancellationToken cancellationToken)
        {
            if (CurrentPrimaryStageKey is not null &&
                _activeStages.TryGetValue(CurrentPrimaryStageKey, out var primaryStage))
            {
                await primaryStage.Writer.WriteLineAsync(line.AsMemory(), cancellationToken);
                primaryStage.Entry.LineCount++;
                _dirty = true;
            }

            if (CurrentSubStageKey is not null &&
                _activeStages.TryGetValue(CurrentSubStageKey, out var subStage))
            {
                await subStage.Writer.WriteLineAsync(line.AsMemory(), cancellationToken);
                subStage.Entry.LineCount++;
                _dirty = true;
            }
        }

        private async Task<ActiveStageContext> OpenStageAsync(
            string stageKey,
            BuildStageLogKind kind,
            string displayName,
            string? parentStageKey,
            ProcessCommand command,
            string startMarker,
            CancellationToken cancellationToken)
        {
            var entry = new BuildStageLogManifestEntry
            {
                StageKey = stageKey,
                Kind = kind,
                DisplayName = displayName,
                ParentStageKey = parentStageKey,
                Order = _nextOrder++,
                Status = BuildStageLogStatus.Running,
                StartedAtUtc = DateTimeOffset.UtcNow,
                LogRelativePath = $"{stageKey}/output.log",
                CommandRelativePath = $"{stageKey}/command.txt",
                EnvironmentRelativePath = command.EnvironmentVariables is { Count: > 0 }
                    ? $"{stageKey}/environment.json"
                    : null
            };

            _manifest.Stages.Add(entry);
            Directory.CreateDirectory(Path.Combine(stageLogsRootPath, stageKey));
            Directory.CreateDirectory(Path.Combine(stageLogsRootPath, stageKey, "artifacts"));

            await WriteCommandFileAsync(entry, command, cancellationToken);

            var logPath = ResolveStageFilePath(entry.LogRelativePath);
            var stream = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            var context = new ActiveStageContext(entry, stream, writer, startMarker);
            _activeStages[stageKey] = context;
            _dirty = true;
            _stateChanged = true;
            return context;
        }

        private async Task CloseStageAsync(string stageKey, BuildStageLogStatus status, CancellationToken cancellationToken)
        {
            if (!_activeStages.TryGetValue(stageKey, out var context))
            {
                return;
            }

            await CaptureArtifactsAsync(context, cancellationToken);
            context.Entry.Status = status;
            context.Entry.FinishedAtUtc = DateTimeOffset.UtcNow;
            await context.Writer.FlushAsync(cancellationToken);
            await context.Writer.DisposeAsync();
            await context.Stream.DisposeAsync();
            _activeStages.Remove(stageKey);
            _dirty = true;
            _stateChanged = true;
        }

        private async Task CaptureArtifactsAsync(ActiveStageContext context, CancellationToken cancellationToken)
        {
            if (context.Entry.Kind == BuildStageLogKind.UBT)
            {
                foreach (var toolLogPath in context.PendingToolLogPaths)
                {
                    await CopyArtifactIfExistsAsync(
                        context,
                        toolLogPath,
                        category: "tool-log",
                        label: Path.GetFileName(toolLogPath),
                        cancellationToken);
                }
            }

            if (!Directory.Exists(uatLogRootPath))
            {
                return;
            }

            var startUtc = context.Entry.StartedAtUtc.UtcDateTime.AddMinutes(-1);
            var endUtc = DateTime.UtcNow.AddMinutes(1);
            foreach (var filePath in Directory.EnumerateFiles(uatLogRootPath))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var lastWriteUtc = File.GetLastWriteTimeUtc(filePath);
                if (lastWriteUtc < startUtc || lastWriteUtc > endUtc)
                {
                    continue;
                }

                var fileName = Path.GetFileName(filePath);
                if (context.Entry.Kind == BuildStageLogKind.Package && FinalCopyArtifacts.IsMatch(fileName))
                {
                    await CopyArtifactIfExistsAsync(context, filePath, "final-copy", fileName, cancellationToken);
                }
                else if (context.Entry.Kind == BuildStageLogKind.Pak && PakArtifacts.IsMatch(fileName))
                {
                    await CopyArtifactIfExistsAsync(context, filePath, "pak-input", fileName, cancellationToken);
                }
                else if (context.Entry.Kind == BuildStageLogKind.IoStore && IoStoreArtifacts.IsMatch(fileName))
                {
                    await CopyArtifactIfExistsAsync(context, filePath, "iostore-input", fileName, cancellationToken);
                }
            }
        }

        private async Task CopyArtifactIfExistsAsync(
            ActiveStageContext context,
            string sourcePath,
            string category,
            string label,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) ||
                !File.Exists(sourcePath) ||
                context.CapturedArtifactSources.Contains(sourcePath))
            {
                return;
            }

            var artifactIndex = context.Entry.InputArtifacts.Count(item =>
                string.Equals(item.Category, category, StringComparison.OrdinalIgnoreCase)) + 1;
            var artifactKey = $"{category}-{artifactIndex:00}";
            var fileName = Path.GetFileName(sourcePath);
            var destinationFileName = BuildSafeArtifactFileName(artifactKey, fileName);
            var destinationRelativePath = $"{context.Entry.StageKey}/artifacts/{destinationFileName}";
            var destinationPath = ResolveStageFilePath(destinationRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

            await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            await input.CopyToAsync(output, cancellationToken);

            context.Entry.InputArtifacts.Add(new BuildStageLogArtifactEntry
            {
                ArtifactKey = artifactKey,
                Label = label,
                Category = category,
                FileName = fileName,
                SizeBytes = output.Length,
                RelativePath = destinationRelativePath
            });

            context.CapturedArtifactSources.Add(sourcePath);
            _dirty = true;
            _stateChanged = true;
        }

        private async Task PersistManifestAsync(CancellationToken cancellationToken)
        {
            if (!_dirty)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            var json = JsonSerializer.Serialize(_manifest, ManifestJsonOptions);

            await ManifestWriteLock.WaitAsync(cancellationToken);
            try
            {
                await using var stream = new FileStream(manifestPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
                stream.SetLength(0);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
                await writer.WriteAsync(json.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
                await stream.FlushAsync(cancellationToken);
                _dirty = false;
            }
            finally
            {
                ManifestWriteLock.Release();
            }
        }

        private async Task WriteCommandFileAsync(
            BuildStageLogManifestEntry entry,
            ProcessCommand command,
            CancellationToken cancellationToken)
        {
            var commandPath = ResolveStageFilePath(entry.CommandRelativePath);
            var builder = new StringBuilder();
            builder.AppendLine($"StartedAtUtc: {entry.StartedAtUtc:O}");
            builder.AppendLine($"FileName: {command.FileName}");
            builder.AppendLine($"WorkingDirectory: {command.WorkingDirectory}");
            builder.AppendLine($"DisplayString: {command.DisplayString}");

            if (command.Arguments.Count > 0)
            {
                builder.AppendLine("Arguments:");
                for (var index = 0; index < command.Arguments.Count; index++)
                {
                    builder.AppendLine($"  [{index}] {command.Arguments[index]}");
                }
            }

            await File.WriteAllTextAsync(commandPath, builder.ToString(), new UTF8Encoding(false), cancellationToken);

            if (entry.EnvironmentRelativePath is null || command.EnvironmentVariables is not { Count: > 0 })
            {
                return;
            }

            var environmentPath = ResolveStageFilePath(entry.EnvironmentRelativePath);
            var environmentJson = JsonSerializer.Serialize(command.EnvironmentVariables, ManifestJsonOptions);
            await File.WriteAllTextAsync(environmentPath, environmentJson, new UTF8Encoding(false), cancellationToken);
        }

        private static ProcessCommand CloneWithDisplay(ProcessCommand command, string displayString)
        {
            return command with { DisplayString = displayString };
        }

        private static string BuildSafeArtifactFileName(string artifactKey, string originalFileName)
        {
            var invalidChars = Path.GetInvalidFileNameChars().ToHashSet();
            var builder = new StringBuilder(originalFileName.Length);
            foreach (var character in originalFileName)
            {
                builder.Append(invalidChars.Contains(character) ? '_' : character);
            }

            return $"{artifactKey}-{builder}";
        }

        private string ResolveStageFilePath(string relativePath)
        {
            return Path.Combine(stageLogsRootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }


        private int NextKindIndex(BuildStageLogKind kind)
        {
            _kindCounts.TryGetValue(kind, out var current);
            current++;
            _kindCounts[kind] = current;
            return current;
        }

        private (BuildStageLogKind? Kind, string StartMarker) DetectSubStage(string line, BuildStageLogKind currentPrimaryKind)
        {
            var trimmedLine = line.Trim();
            if (UbtStart.IsMatch(trimmedLine))
            {
                return (BuildStageLogKind.UBT, trimmedLine);
            }

            if (ShouldTreatAsIoStore(trimmedLine, currentPrimaryKind))
            {
                return (BuildStageLogKind.IoStore, trimmedLine);
            }

            if (PakStart.IsMatch(trimmedLine))
            {
                return (BuildStageLogKind.Pak, trimmedLine);
            }

            if (ioStoreEnabled &&
                currentPrimaryKind == BuildStageLogKind.Package &&
                IoStoreStart.IsMatch(trimmedLine))
            {
                return (BuildStageLogKind.IoStore, trimmedLine);
            }

            return (null, trimmedLine);
        }

        private BuildPhase NormalizePrimaryPhase(BuildPhase phase)
        {
            if (!PhaseOrder.ContainsKey(phase))
            {
                return _furthestPrimaryPhase ?? BuildPhase.Build;
            }

            if (_furthestPrimaryPhase is null)
            {
                _furthestPrimaryPhase = phase;
                return phase;
            }

            if (PhaseOrder[phase] < PhaseOrder[_furthestPrimaryPhase.Value])
            {
                return _furthestPrimaryPhase.Value;
            }

            _furthestPrimaryPhase = phase;
            return phase;
        }

        private bool ShouldTreatAsIoStore(string line, BuildStageLogKind currentPrimaryKind)
        {
            return ioStoreEnabled &&
                   currentPrimaryKind == BuildStageLogKind.Package &&
                   UnrealPakIoStoreStart.IsMatch(line);
        }

        private static bool TryGetToolLogPath(string line, out string toolLogPath)
        {
            toolLogPath = string.Empty;
            var match = ToolLogLine.Match(line.Trim());
            if (!match.Success)
            {
                return false;
            }

            toolLogPath = match.Groups[1].Value.Trim().Trim('"');
            return toolLogPath.Length > 0;
        }

        private static BuildStageLogKind MapPrimaryKind(BuildPhase phase)
        {
            return phase switch
            {
                BuildPhase.SourceSync => BuildStageLogKind.SourceSync,
                BuildPhase.Build => BuildStageLogKind.Build,
                BuildPhase.Cook => BuildStageLogKind.Cook,
                BuildPhase.Stage => BuildStageLogKind.Stage,
                BuildPhase.Package => BuildStageLogKind.Package,
                BuildPhase.Archive => BuildStageLogKind.Archive,
                BuildPhase.Zip => BuildStageLogKind.Zip,
                _ => BuildStageLogKind.Build
            };
        }

        private static string BuildPrimaryStageKey(BuildStageLogKind kind)
        {
            return kind switch
            {
                BuildStageLogKind.SourceSync => "source-sync",
                BuildStageLogKind.Build => "build",
                BuildStageLogKind.Cook => "cook",
                BuildStageLogKind.Stage => "stage",
                BuildStageLogKind.Package => "package",
                BuildStageLogKind.Archive => "archive",
                BuildStageLogKind.Zip => "zip",
                _ => kind.ToString().ToLowerInvariant()
            };
        }

        private static string GetPrimaryStageDisplayName(BuildStageLogKind kind)
        {
            return kind switch
            {
                BuildStageLogKind.SourceSync => "Source Sync",
                BuildStageLogKind.IoStore => "IoStore",
                _ => kind.ToString()
            };
        }

        private sealed class ActiveStageContext(
            BuildStageLogManifestEntry entry,
            FileStream stream,
            StreamWriter writer,
            string startMarker)
        {
            public BuildStageLogManifestEntry Entry { get; } = entry;

            public FileStream Stream { get; } = stream;

            public StreamWriter Writer { get; } = writer;

            public string StartMarker { get; } = startMarker;

            public HashSet<string> PendingToolLogPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

            public HashSet<string> CapturedArtifactSources { get; } = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    public sealed class BuildStageLogManifest
    {
        public List<BuildStageLogManifestEntry> Stages { get; set; } = new();
    }

    public sealed class BuildStageLogManifestEntry
    {
        public string StageKey { get; set; } = string.Empty;

        public BuildStageLogKind Kind { get; set; }

        public string DisplayName { get; set; } = string.Empty;

        public string? ParentStageKey { get; set; }

        public int Order { get; set; }

        public BuildStageLogStatus Status { get; set; }

        public DateTimeOffset StartedAtUtc { get; set; }

        public DateTimeOffset? FinishedAtUtc { get; set; }

        public string LogRelativePath { get; set; } = string.Empty;

        public long LineCount { get; set; }

        public string CommandRelativePath { get; set; } = string.Empty;

        public string? EnvironmentRelativePath { get; set; }

        public List<BuildStageLogArtifactEntry> InputArtifacts { get; set; } = new();
    }

    public sealed class BuildStageLogArtifactEntry
    {
        public string ArtifactKey { get; set; } = string.Empty;

        public string Label { get; set; } = string.Empty;

        public string Category { get; set; } = string.Empty;

        public string FileName { get; set; } = string.Empty;

        public long SizeBytes { get; set; }

        public string RelativePath { get; set; } = string.Empty;
    }
}
