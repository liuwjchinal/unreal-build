using Backend.Contracts;
using Backend.Models;
using Backend.Options;
using Backend.Services;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Backend.Tests;

public sealed class BuildStageLogServiceTests : IDisposable
{
    private readonly string _rootPath;
    private readonly StoragePaths _storagePaths;
    private readonly BuildStageLogService _service;

    public BuildStageLogServiceTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "backend-stage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootPath);
        _storagePaths = StoragePaths.Create(new AppOptions { StorageRoot = _rootPath }, _rootPath);
        _storagePaths.EnsureCreated();
        _service = new BuildStageLogService(_storagePaths, new BuildLogReader());
    }

    [Fact]
    public async Task CaptureSession_SplitsPrimaryAndSubStages_WithStableKeys()
    {
        var build = CreateBuildRecord();
        var project = CreateProject();
        Directory.CreateDirectory(Path.Combine(project.EngineRootPath, "Engine", "Programs", "AutomationTool", "Saved", "Logs"));

        await using var session = _service.CreateCaptureSession(build, project);
        await session.InitializeAsync(CancellationToken.None);

        var uatCommand = new ProcessCommand(
            "cmd.exe",
            ["/c", "RunUAT.bat", "BuildCookRun"],
            build.BuildRootPath,
            "cmd.exe /c RunUAT.bat BuildCookRun");

        await session.StartStandaloneStageAsync(BuildStageLogKind.Build, uatCommand, CancellationToken.None);
        await session.HandleUatLineAsync("Running: UnrealBuildTool.dll -Target=Game", BuildPhase.Build, uatCommand, CancellationToken.None);
        await session.HandleUatLineAsync("[1/10] Compile Foo", BuildPhase.Build, uatCommand, CancellationToken.None);
        await session.HandleUatLineAsync("COOK COMMAND STARTED", BuildPhase.Cook, uatCommand, CancellationToken.None);
        await session.HandleUatLineAsync("Cooked packages 9 Packages Remain 10 Total 19", BuildPhase.Cook, uatCommand, CancellationToken.None);
        await session.HandleUatLineAsync("PACKAGE COMMAND STARTED", BuildPhase.Package, uatCommand, CancellationToken.None);
        await session.HandleUatLineAsync("Running: UnrealPak.exe -Create=PakCommands.txt", BuildPhase.Package, uatCommand, CancellationToken.None);
        await session.HandleUatLineAsync("Running: UnrealPak.exe -Create=PakCommands_2.txt", BuildPhase.Package, uatCommand, CancellationToken.None);
        await session.HandleUatLineAsync("ARCHIVE COMMAND STARTED", BuildPhase.Archive, uatCommand, CancellationToken.None);
        await session.CompleteActiveStagesAsync(BuildStageLogStatus.Completed, CancellationToken.None);
        await session.FlushAsync(CancellationToken.None);

        var list = await _service.ReadListAsync(build, CancellationToken.None);
        var stageKeys = list.Stages.Select(stage => stage.StageKey).ToArray();

        Assert.Equal(
            ["build", "ubt-01", "cook", "package", "pak-01", "pak-02", "archive"],
            stageKeys);

        var pakChildren = list.Stages.Where(stage => stage.Kind == BuildStageLogKind.Pak).ToArray();
        Assert.All(pakChildren, stage => Assert.Equal("package", stage.ParentStageKey));

        var buildSnapshot = await _service.ReadStageLogAsync(build, "build", 4000, CancellationToken.None);
        var ubtSnapshot = await _service.ReadStageLogAsync(build, "ubt-01", 4000, CancellationToken.None);
        Assert.NotNull(buildSnapshot);
        Assert.NotNull(ubtSnapshot);
        Assert.Contains(buildSnapshot!.Lines, line => line.Contains("Compile Foo", StringComparison.Ordinal));
        Assert.Contains(ubtSnapshot!.Lines, line => line.Contains("Compile Foo", StringComparison.Ordinal));
        Assert.DoesNotContain(ubtSnapshot.Lines, line => line.Contains("COOK COMMAND STARTED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CaptureSession_DoesNotReopenPrimaryStages_WhenTailLogsRegressPhase()
    {
        var build = CreateBuildRecord();
        var project = CreateProject();
        Directory.CreateDirectory(Path.Combine(project.EngineRootPath, "Engine", "Programs", "AutomationTool", "Saved", "Logs"));

        await using var session = _service.CreateCaptureSession(build, project);
        await session.InitializeAsync(CancellationToken.None);

        var uatCommand = new ProcessCommand(
            "cmd.exe",
            ["/c", "RunUAT.bat", "BuildCookRun"],
            build.BuildRootPath,
            "cmd.exe /c RunUAT.bat BuildCookRun");

        await session.StartStandaloneStageAsync(BuildStageLogKind.Build, uatCommand, CancellationToken.None);
        await session.HandleUatLineAsync("COOK COMMAND STARTED", BuildPhase.Cook, uatCommand, CancellationToken.None);
        await session.HandleUatLineAsync("********** STAGE COMMAND STARTED **********", BuildPhase.Stage, uatCommand, CancellationToken.None);
        await session.HandleUatLineAsync("Running: UnrealPak.exe -Create=PakCommands.txt", BuildPhase.Package, uatCommand, CancellationToken.None);
        await session.HandleUatLineAsync("Copying NonUFSFiles to staging directory: F:\\Test\\Saved\\StagedBuilds\\Windows", BuildPhase.Stage, uatCommand, CancellationToken.None);
        await session.HandleUatLineAsync("********** PACKAGE COMMAND STARTED **********", BuildPhase.Package, uatCommand, CancellationToken.None);
        await session.HandleUatLineAsync("ARCHIVE COMMAND STARTED", BuildPhase.Archive, uatCommand, CancellationToken.None);
        await session.CompleteActiveStagesAsync(BuildStageLogStatus.Completed, CancellationToken.None);
        await session.FlushAsync(CancellationToken.None);

        var list = await _service.ReadListAsync(build, CancellationToken.None);

        Assert.Equal(
            ["build", "cook", "stage", "package", "pak-01", "archive"],
            list.Stages.Select(stage => stage.StageKey).ToArray());
        Assert.Single(list.Stages, stage => stage.StageKey == "stage");
        Assert.Single(list.Stages, stage => stage.StageKey == "package");
    }

    [Fact]
    public async Task CaptureSession_ClassifiesGlobalContainerUnrealPakAsIoStore()
    {
        var build = CreateBuildRecord();
        var project = CreateProject();
        Directory.CreateDirectory(Path.Combine(project.EngineRootPath, "Engine", "Programs", "AutomationTool", "Saved", "Logs"));

        await using var session = _service.CreateCaptureSession(build, project);
        await session.InitializeAsync(CancellationToken.None);

        var uatCommand = new ProcessCommand(
            "cmd.exe",
            ["/c", "RunUAT.bat", "BuildCookRun"],
            build.BuildRootPath,
            "cmd.exe /c RunUAT.bat BuildCookRun");

        await session.StartStandaloneStageAsync(BuildStageLogKind.Build, uatCommand, CancellationToken.None);
        await session.HandleUatLineAsync("PACKAGE COMMAND STARTED", BuildPhase.Package, uatCommand, CancellationToken.None);
        await session.HandleUatLineAsync("Running: UnrealPak.exe -Create=PakCommands.txt", BuildPhase.Package, uatCommand, CancellationToken.None);
        await session.HandleUatLineAsync(
            "Running: UnrealPak.exe -CreateGlobalContainer=\"F:\\Test\\global.utoc\" -PackageStoreManifest=\"F:\\Test\\packagestore.manifest\" -Commands=\"F:\\Logs\\IoStoreCommands.txt\"",
            BuildPhase.Package,
            uatCommand,
            CancellationToken.None);
        await session.CompleteActiveStagesAsync(BuildStageLogStatus.Completed, CancellationToken.None);
        await session.FlushAsync(CancellationToken.None);

        var list = await _service.ReadListAsync(build, CancellationToken.None);

        Assert.Equal(
            ["build", "package", "pak-01", "iostore-01"],
            list.Stages.Select(stage => stage.StageKey).ToArray());
        Assert.Equal(BuildStageLogKind.IoStore, list.Stages.Single(stage => stage.StageKey == "iostore-01").Kind);
        Assert.Equal("package", list.Stages.Single(stage => stage.StageKey == "iostore-01").ParentStageKey);
    }

    [Fact]
    public async Task CaptureSession_DoesNotTreatSkipIoStoreArgumentAsIoStoreStage()
    {
        var build = CreateBuildRecord(ioStore: false);
        var project = CreateProject();
        Directory.CreateDirectory(Path.Combine(project.EngineRootPath, "Engine", "Programs", "AutomationTool", "Saved", "Logs"));

        await using var session = _service.CreateCaptureSession(build, project);
        await session.InitializeAsync(CancellationToken.None);

        var uatCommand = new ProcessCommand(
            "cmd.exe",
            ["/c", "RunUAT.bat", "BuildCookRun"],
            build.BuildRootPath,
            "cmd.exe /c RunUAT.bat BuildCookRun");

        await session.StartStandaloneStageAsync(BuildStageLogKind.Build, uatCommand, CancellationToken.None);
        await session.HandleUatLineAsync(
            "Parsing command line: BuildCookRun -project=F:\\Test\\Game.uproject -build -cook -stage -package -archive -pak -skipiostore",
            BuildPhase.Build,
            uatCommand,
            CancellationToken.None);
        await session.HandleUatLineAsync("Running: UnrealBuildTool.dll -Target=Game", BuildPhase.Build, uatCommand, CancellationToken.None);
        await session.CompleteActiveStagesAsync(BuildStageLogStatus.Completed, CancellationToken.None);
        await session.FlushAsync(CancellationToken.None);

        var list = await _service.ReadListAsync(build, CancellationToken.None);

        Assert.Equal(["build", "ubt-01"], list.Stages.Select(stage => stage.StageKey).ToArray());
        Assert.DoesNotContain(list.Stages, stage => stage.Kind == BuildStageLogKind.IoStore);
    }

    [Fact]
    public async Task CaptureSession_DoesNotCreateIoStoreStage_WhenIoStoreDisabled()
    {
        var build = CreateBuildRecord(ioStore: false);
        var project = CreateProject();
        Directory.CreateDirectory(Path.Combine(project.EngineRootPath, "Engine", "Programs", "AutomationTool", "Saved", "Logs"));

        await using var session = _service.CreateCaptureSession(build, project);
        await session.InitializeAsync(CancellationToken.None);

        var uatCommand = new ProcessCommand(
            "cmd.exe",
            ["/c", "RunUAT.bat", "BuildCookRun"],
            build.BuildRootPath,
            "cmd.exe /c RunUAT.bat BuildCookRun");

        await session.StartStandaloneStageAsync(BuildStageLogKind.Build, uatCommand, CancellationToken.None);
        await session.HandleUatLineAsync("PACKAGE COMMAND STARTED", BuildPhase.Package, uatCommand, CancellationToken.None);
        await session.HandleUatLineAsync("Running: UnrealPak.exe -Create=PakCommands.txt", BuildPhase.Package, uatCommand, CancellationToken.None);
        await session.HandleUatLineAsync(
            "Running: UnrealPak.exe -CreateGlobalContainer=\"F:\\Test\\global.utoc\" -PackageStoreManifest=\"F:\\Test\\packagestore.manifest\" -Commands=\"F:\\Logs\\IoStoreCommands.txt\"",
            BuildPhase.Package,
            uatCommand,
            CancellationToken.None);
        await session.CompleteActiveStagesAsync(BuildStageLogStatus.Completed, CancellationToken.None);
        await session.FlushAsync(CancellationToken.None);

        var list = await _service.ReadListAsync(build, CancellationToken.None);

        Assert.Equal(["build", "package", "pak-01", "pak-02"], list.Stages.Select(stage => stage.StageKey).ToArray());
        Assert.DoesNotContain(list.Stages, stage => stage.Kind == BuildStageLogKind.IoStore);
    }

    [Fact]
    public async Task CaptureSession_DoesNotSplitPakStageOnIoStoreTextInPakOutput()
    {
        var build = CreateBuildRecord(ioStore: false);
        var project = CreateProject();
        Directory.CreateDirectory(Path.Combine(project.EngineRootPath, "Engine", "Programs", "AutomationTool", "Saved", "Logs"));

        await using var session = _service.CreateCaptureSession(build, project);
        await session.InitializeAsync(CancellationToken.None);

        var uatCommand = new ProcessCommand(
            "cmd.exe",
            ["/c", "RunUAT.bat", "BuildCookRun"],
            build.BuildRootPath,
            "cmd.exe /c RunUAT.bat BuildCookRun");

        await session.StartStandaloneStageAsync(BuildStageLogKind.Build, uatCommand, CancellationToken.None);
        await session.HandleUatLineAsync("PACKAGE COMMAND STARTED", BuildPhase.Package, uatCommand, CancellationToken.None);
        await session.HandleUatLineAsync("Running: UnrealPak.exe -Create=PakCommands.txt", BuildPhase.Package, uatCommand, CancellationToken.None);
        await session.HandleUatLineAsync(
            "OodleDataCompression: Display: Oodle v2.9.14 format for pak/iostore with method=Kraken, level=4=Normal",
            BuildPhase.Package,
            uatCommand,
            CancellationToken.None);
        await session.HandleUatLineAsync("LogIoStore: Display: Using command list file: 'F:\\Logs\\IoStoreCommands.txt'", BuildPhase.Package, uatCommand, CancellationToken.None);
        await session.CompleteActiveStagesAsync(BuildStageLogStatus.Completed, CancellationToken.None);
        await session.FlushAsync(CancellationToken.None);

        var list = await _service.ReadListAsync(build, CancellationToken.None);
        var pakSnapshot = await _service.ReadStageLogAsync(build, "pak-01", 4000, CancellationToken.None);

        Assert.Equal(["build", "package", "pak-01"], list.Stages.Select(stage => stage.StageKey).ToArray());
        Assert.DoesNotContain(list.Stages, stage => stage.Kind == BuildStageLogKind.IoStore);
        Assert.NotNull(pakSnapshot);
        Assert.Contains(pakSnapshot!.Lines, line => line.Contains("pak/iostore", StringComparison.Ordinal));
        Assert.Contains(pakSnapshot.Lines, line => line.Contains("LogIoStore", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CaptureSession_CopiesToolLogAndCommandArtifacts()
    {
        var build = CreateBuildRecord();
        var project = CreateProject();
        var uatLogRoot = Path.Combine(project.EngineRootPath, "Engine", "Programs", "AutomationTool", "Saved", "Logs");
        Directory.CreateDirectory(uatLogRoot);

        var toolLogPath = Path.Combine(uatLogRoot, "UBA-Test.txt");
        await File.WriteAllTextAsync(toolLogPath, "tool log");

        var pakCommandsPath = Path.Combine(uatLogRoot, "PakCommands.txt");
        await File.WriteAllTextAsync(pakCommandsPath, "pak input");
        File.SetLastWriteTimeUtc(toolLogPath, DateTime.UtcNow);
        File.SetLastWriteTimeUtc(pakCommandsPath, DateTime.UtcNow);

        await using var session = _service.CreateCaptureSession(build, project);
        await session.InitializeAsync(CancellationToken.None);

        var uatCommand = new ProcessCommand(
            "cmd.exe",
            ["/c", "RunUAT.bat", "BuildCookRun"],
            build.BuildRootPath,
            "cmd.exe /c RunUAT.bat BuildCookRun",
            new Dictionary<string, string> { ["UE-SDK"] = "test" });

        await session.StartStandaloneStageAsync(BuildStageLogKind.Build, uatCommand, CancellationToken.None);
        await session.HandleUatLineAsync("Running: UnrealBuildTool.dll -Target=Game", BuildPhase.Build, uatCommand, CancellationToken.None);
        await session.HandleUatLineAsync($"Log file: {toolLogPath}", BuildPhase.Build, uatCommand, CancellationToken.None);
        await session.HandleUatLineAsync("PACKAGE COMMAND STARTED", BuildPhase.Package, uatCommand, CancellationToken.None);
        await session.HandleUatLineAsync("Running: UnrealPak.exe -Create=PakCommands.txt", BuildPhase.Package, uatCommand, CancellationToken.None);
        await session.CompleteActiveStagesAsync(BuildStageLogStatus.Completed, CancellationToken.None);
        await session.FlushAsync(CancellationToken.None);

        var ubt = await _service.ReadStageLogAsync(build, "ubt-01", 4000, CancellationToken.None);
        var pak = await _service.ReadStageLogAsync(build, "pak-01", 4000, CancellationToken.None);

        Assert.NotNull(ubt);
        Assert.NotNull(pak);
        Assert.Contains(ubt!.Stage.InputArtifacts, artifact => artifact.Category == "tool-log");
        Assert.Contains(pak!.Stage.InputArtifacts, artifact => artifact.Category == "pak-input");

        var commandFile = Path.Combine(build.BuildRootPath, "stage-logs", "ubt-01", "command.txt");
        var environmentFile = Path.Combine(build.BuildRootPath, "stage-logs", "ubt-01", "environment.json");
        Assert.True(File.Exists(commandFile));
        Assert.True(File.Exists(environmentFile));
    }

    [Fact]
    public async Task MarkDanglingStagesAsync_MarksRunningStagesInterrupted()
    {
        var build = CreateBuildRecord();
        var project = CreateProject();
        Directory.CreateDirectory(Path.Combine(project.EngineRootPath, "Engine", "Programs", "AutomationTool", "Saved", "Logs"));

        await using (var session = _service.CreateCaptureSession(build, project))
        {
            await session.InitializeAsync(CancellationToken.None);
            await session.StartStandaloneStageAsync(
                BuildStageLogKind.SourceSync,
                new ProcessCommand("svn", ["update"], build.BuildRootPath, "svn update"),
                CancellationToken.None);
            await session.WriteStandaloneLineAsync("syncing", CancellationToken.None);
            await session.FlushAsync(CancellationToken.None);
        }

        await _service.MarkDanglingStagesAsync(build.BuildRootPath, BuildStageLogStatus.Interrupted, CancellationToken.None);
        var list = await _service.ReadListAsync(build, CancellationToken.None);

        Assert.Single(list.Stages);
        Assert.Equal(BuildStageLogStatus.Interrupted, list.Stages[0].Status);
        Assert.NotNull(list.Stages[0].FinishedAtUtc);
    }

    [Fact]
    public async Task PersistManifest_AllowsConcurrentReadHandle()
    {
        var build = CreateBuildRecord();
        var project = CreateProject();
        Directory.CreateDirectory(Path.Combine(project.EngineRootPath, "Engine", "Programs", "AutomationTool", "Saved", "Logs"));

        await using var session = _service.CreateCaptureSession(build, project);
        await session.InitializeAsync(CancellationToken.None);

        var manifestPath = Path.Combine(build.BuildRootPath, "stage-logs", "manifest.json");
        using var reader = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        await session.StartStandaloneStageAsync(
            BuildStageLogKind.SourceSync,
            new ProcessCommand("svn", ["update"], build.BuildRootPath, "svn update"),
            CancellationToken.None);
        await session.WriteStandaloneLineAsync("syncing", CancellationToken.None);
        await session.FlushAsync(CancellationToken.None);

        var manifest = await ReadManifestAsync(manifestPath);
        Assert.Single(manifest.Stages);
        Assert.Equal("source-sync", manifest.Stages[0].StageKey);
    }

    [Fact]
    public async Task ReadListAsync_FiltersLegacyInvalidIoStoreStages()
    {
        var build = CreateBuildRecord(ioStore: false);
        Directory.CreateDirectory(Path.Combine(build.BuildRootPath, "stage-logs", "build"));
        Directory.CreateDirectory(Path.Combine(build.BuildRootPath, "stage-logs", "iostore-01"));
        Directory.CreateDirectory(Path.Combine(build.BuildRootPath, "stage-logs", "package"));
        Directory.CreateDirectory(Path.Combine(build.BuildRootPath, "stage-logs", "iostore-02"));

        await File.WriteAllTextAsync(
            Path.Combine(build.BuildRootPath, "stage-logs", "build", "output.log"),
            "BUILD COMMAND STARTED");
        await File.WriteAllTextAsync(
            Path.Combine(build.BuildRootPath, "stage-logs", "iostore-01", "output.log"),
            "Parsing command line: BuildCookRun -pak -skipiostore");
        await File.WriteAllTextAsync(
            Path.Combine(build.BuildRootPath, "stage-logs", "package", "output.log"),
            "PACKAGE COMMAND STARTED");
        await File.WriteAllTextAsync(
            Path.Combine(build.BuildRootPath, "stage-logs", "iostore-02", "output.log"),
            "Running: UnrealPak.exe -CreateGlobalContainer=\"F:\\Test\\global.utoc\" -PackageStoreManifest=\"F:\\Test\\packagestore.manifest\"");

        var manifest = new BuildStageLogService.BuildStageLogManifest
        {
            Stages =
            [
                new BuildStageLogService.BuildStageLogManifestEntry
                {
                    StageKey = "build",
                    Kind = BuildStageLogKind.Build,
                    DisplayName = "Build",
                    Order = 1,
                    Status = BuildStageLogStatus.Completed,
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    FinishedAtUtc = DateTimeOffset.UtcNow,
                    LogRelativePath = "build/output.log",
                    CommandRelativePath = "build/command.txt"
                },
                new BuildStageLogService.BuildStageLogManifestEntry
                {
                    StageKey = "iostore-01",
                    Kind = BuildStageLogKind.IoStore,
                    DisplayName = "IoStore",
                    ParentStageKey = "build",
                    Order = 2,
                    Status = BuildStageLogStatus.Completed,
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    FinishedAtUtc = DateTimeOffset.UtcNow,
                    LogRelativePath = "iostore-01/output.log",
                    CommandRelativePath = "iostore-01/command.txt"
                },
                new BuildStageLogService.BuildStageLogManifestEntry
                {
                    StageKey = "package",
                    Kind = BuildStageLogKind.Package,
                    DisplayName = "Package",
                    Order = 3,
                    Status = BuildStageLogStatus.Completed,
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    FinishedAtUtc = DateTimeOffset.UtcNow,
                    LogRelativePath = "package/output.log",
                    CommandRelativePath = "package/command.txt"
                },
                new BuildStageLogService.BuildStageLogManifestEntry
                {
                    StageKey = "iostore-02",
                    Kind = BuildStageLogKind.IoStore,
                    DisplayName = "IoStore #2",
                    ParentStageKey = "package",
                    Order = 4,
                    Status = BuildStageLogStatus.Completed,
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    FinishedAtUtc = DateTimeOffset.UtcNow,
                    LogRelativePath = "iostore-02/output.log",
                    CommandRelativePath = "iostore-02/command.txt"
                }
            ]
        };

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        await File.WriteAllTextAsync(
            Path.Combine(build.BuildRootPath, "stage-logs", "manifest.json"),
            JsonSerializer.Serialize(manifest, options));

        var list = await _service.ReadListAsync(build, CancellationToken.None);

        Assert.Equal(["build", "package"], list.Stages.Select(stage => stage.StageKey).ToArray());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_rootPath, true);
        }
        catch
        {
            // Ignore cleanup failures for temp files on Windows.
        }
    }

    private BuildRecord CreateBuildRecord(bool ioStore = true)
    {
        var buildId = Guid.NewGuid();
        var buildRoot = _storagePaths.ResolveBuildRoot(buildId);
        Directory.CreateDirectory(buildRoot);

        return new BuildRecord
        {
            Id = buildId,
            ProjectId = Guid.NewGuid(),
            BuildRootPath = buildRoot,
            LogFilePath = _storagePaths.ResolveLogPath(buildId),
            QueuedAtUtc = DateTimeOffset.UtcNow,
            Status = BuildStatus.Running,
            CurrentPhase = BuildPhase.Build,
            IoStore = ioStore
        };
    }

    private ProjectConfig CreateProject()
    {
        var engineRoot = Path.Combine(_rootPath, "EngineRoot");
        Directory.CreateDirectory(engineRoot);

        return new ProjectConfig
        {
            Id = Guid.NewGuid(),
            Name = "TestProject",
            WorkingCopyPath = _rootPath,
            UProjectPath = Path.Combine(_rootPath, "Test.uproject"),
            EngineRootPath = engineRoot,
            ArchiveRootPath = Path.Combine(_rootPath, "Archive"),
            ProjectKey = "test-project",
            ProjectFingerprint = "test-fingerprint"
        };
    }

    private static async Task<BuildStageLogService.BuildStageLogManifest> ReadManifestAsync(string manifestPath)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        var json = await File.ReadAllTextAsync(manifestPath);
        return JsonSerializer.Deserialize<BuildStageLogService.BuildStageLogManifest>(json, options)
               ?? new BuildStageLogService.BuildStageLogManifest();
    }
}
