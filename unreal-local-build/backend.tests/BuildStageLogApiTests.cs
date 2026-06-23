using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backend.Contracts;
using Backend.Data;
using Backend.Models;
using Backend.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Backend.Tests;

public sealed class BuildStageLogApiTests
{
    private static readonly JsonSerializerOptions ResponseJsonOptions = CreateResponseJsonOptions();

    [Fact]
    public async Task GetUbaAgentConfig_ReturnsConfiguredEndpoint()
    {
        await using var host = StageLogApiTestHost.Create();

        var response = await host.Client.GetAsync("/api/uba-agent/config");

        response.EnsureSuccessStatusCode();
        var config = await response.Content.ReadFromJsonAsync<UbaAgentConfigDto>(ResponseJsonOptions);
        Assert.NotNull(config);
        Assert.True(config!.Enabled);
        Assert.False(string.IsNullOrWhiteSpace(config.Host));
        Assert.Equal(1345, config.Port);
        Assert.Equal("/api/uba-agent/package", config.PackageDownloadUrl);
        Assert.Contains("uba-agent://join?", config.ProtocolExampleUrl, StringComparison.Ordinal);
        Assert.Contains("UbaAgent.exe -host=", config.ManualCommandExample, StringComparison.Ordinal);
        Assert.False(config.HostAutoDetected);
        Assert.Null(config.HostWarning);
        Assert.Equal(16, config.PortPoolSize);
    }

    [Fact]
    public async Task GetUbaAgentPackage_ReturnsNotFound_WhenSourceMissing()
    {
        await using var host = StageLogApiTestHost.Create();

        var response = await host.Client.GetAsync("/api/uba-agent/package");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetStageLogs_ReturnsNotFound_WhenManifestMissing()
    {
        await using var host = StageLogApiTestHost.Create();
        var build = await host.SeedBuildAsync(BuildStatus.Running, includeStageLogs: false, includeArtifact: false);

        var response = await host.Client.GetAsync($"/api/builds/{build.Id}/stage-logs");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DownloadStageLogArchive_ReturnsConflict_WhenBuildIsRunning()
    {
        await using var host = StageLogApiTestHost.Create();
        var build = await host.SeedBuildAsync(BuildStatus.Running, includeStageLogs: true, includeArtifact: false);

        var response = await host.Client.GetAsync($"/api/builds/{build.Id}/stage-logs/download");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("阶段日志仍在生成中", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StageLogEndpoints_ReturnExpectedPayloads_ForCompletedBuild()
    {
        await using var host = StageLogApiTestHost.Create();
        var build = await host.SeedBuildAsync(BuildStatus.Succeeded, includeStageLogs: true, includeArtifact: true);

        var listResponse = await host.Client.GetAsync($"/api/builds/{build.Id}/stage-logs");
        listResponse.EnsureSuccessStatusCode();
        var list = await listResponse.Content.ReadFromJsonAsync<BuildStageLogListDto>(ResponseJsonOptions);
        Assert.NotNull(list);
        var stage = Assert.Single(list!.Stages);
        Assert.Equal("build", stage.StageKey);
        var artifact = Assert.Single(stage.InputArtifacts);

        var snapshotResponse = await host.Client.GetAsync($"/api/builds/{build.Id}/stage-logs/build?tailLines=1");
        snapshotResponse.EnsureSuccessStatusCode();
        var snapshot = await snapshotResponse.Content.ReadFromJsonAsync<BuildStageLogSnapshotDto>(ResponseJsonOptions);
        Assert.NotNull(snapshot);
        Assert.Equal("build", snapshot!.Stage.StageKey);
        Assert.Equal(1, snapshot.IncludedLines);
        Assert.Equal(2, snapshot.TotalLines);
        Assert.True(snapshot.Truncated);
        Assert.Single(snapshot.Lines);
        Assert.Equal("compile line 2", snapshot.Lines[0]);

        var stageDownload = await host.Client.GetAsync($"/api/builds/{build.Id}/stage-logs/build/download");
        stageDownload.EnsureSuccessStatusCode();
        var stageLogText = await stageDownload.Content.ReadAsStringAsync();
        Assert.Contains("compile line 1", stageLogText, StringComparison.Ordinal);

        var artifactDownload = await host.Client.GetAsync(
            $"/api/builds/{build.Id}/stage-logs/build/artifacts/{artifact.ArtifactKey}/download");
        artifactDownload.EnsureSuccessStatusCode();
        var artifactText = await artifactDownload.Content.ReadAsStringAsync();
        Assert.Equal("tool log", artifactText);

        var archiveResponse = await host.Client.GetAsync($"/api/builds/{build.Id}/stage-logs/download");
        archiveResponse.EnsureSuccessStatusCode();
        var archiveBytes = await archiveResponse.Content.ReadAsByteArrayAsync();
        using var archiveStream = new MemoryStream(archiveBytes);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        var archiveEntries = archive.Entries.Select(entry => entry.FullName).ToArray();

        Assert.Contains("stage-logs/manifest.json", archiveEntries);
        Assert.Contains("stage-logs/build/output.log", archiveEntries);
        Assert.Contains("stage-logs/build/command.txt", archiveEntries);
        Assert.Contains("stage-logs/build/artifacts/tool.log", archiveEntries);
    }

    [Fact]
    public async Task BuildDownload_ReturnsArchiveZip_WhenExistingZipWasCleaned()
    {
        await using var host = StageLogApiTestHost.Create();
        var build = await host.SeedBuildAsync(BuildStatus.Succeeded, includeStageLogs: false, includeArtifact: false);
        Directory.CreateDirectory(build.ArchiveDirectoryPath);
        await File.WriteAllTextAsync(
            Path.Combine(build.ArchiveDirectoryPath, "artifact.txt"),
            "packaged content",
            new UTF8Encoding(false));

        var detailResponse = await host.Client.GetAsync($"/api/builds/{build.Id}");
        detailResponse.EnsureSuccessStatusCode();
        var detail = await detailResponse.Content.ReadFromJsonAsync<BuildDetailDto>(ResponseJsonOptions);
        Assert.NotNull(detail);
        Assert.Equal($"/api/builds/{build.Id}/download", detail!.DownloadUrl);

        var downloadResponse = await host.Client.GetAsync(detail.DownloadUrl);
        downloadResponse.EnsureSuccessStatusCode();
        var zipBytes = await downloadResponse.Content.ReadAsByteArrayAsync();
        using var archiveStream = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        var entry = Assert.Single(archive.Entries);
        Assert.Equal("artifact.txt", entry.FullName);
    }

    [Fact]
    public async Task BuildDownload_AllowsConcurrentArchiveFallbackRequests()
    {
        await using var host = StageLogApiTestHost.Create();
        var build = await host.SeedBuildAsync(BuildStatus.Succeeded, includeStageLogs: false, includeArtifact: false);
        Directory.CreateDirectory(build.ArchiveDirectoryPath);
        await File.WriteAllTextAsync(
            Path.Combine(build.ArchiveDirectoryPath, "artifact.txt"),
            "packaged content",
            new UTF8Encoding(false));

        var responses = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => host.Client.GetAsync($"/api/builds/{build.Id}/download")));

        Assert.All(responses, response => response.EnsureSuccessStatusCode());
        var zipBytes = await responses[0].Content.ReadAsByteArrayAsync();
        using var archiveStream = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        var entry = Assert.Single(archive.Entries);
        Assert.Equal("artifact.txt", entry.FullName);
    }

    [Fact]
    public async Task BuildDownload_ReturnsNotFound_ForArchiveFallbackOutsideRecentRetention()
    {
        await using var host = StageLogApiTestHost.Create();
        var olderBuild = await host.SeedBuildAsync(BuildStatus.Succeeded, includeStageLogs: false, includeArtifact: false);
        Directory.CreateDirectory(olderBuild.ArchiveDirectoryPath);
        await File.WriteAllTextAsync(
            Path.Combine(olderBuild.ArchiveDirectoryPath, "old-artifact.txt"),
            "old packaged content",
            new UTF8Encoding(false));
        await host.SeedSucceededBuildsAsync(olderBuild.ProjectId, count: 30, newerThanUtc: olderBuild.FinishedAtUtc!.Value);

        var detailResponse = await host.Client.GetAsync($"/api/builds/{olderBuild.Id}");
        detailResponse.EnsureSuccessStatusCode();
        var detail = await detailResponse.Content.ReadFromJsonAsync<BuildDetailDto>(ResponseJsonOptions);
        Assert.NotNull(detail);
        Assert.Null(detail!.DownloadUrl);

        var downloadResponse = await host.Client.GetAsync($"/api/builds/{olderBuild.Id}/download");
        Assert.Equal(HttpStatusCode.NotFound, downloadResponse.StatusCode);
    }

    private static JsonSerializerOptions CreateResponseJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed class StageLogApiTestHost : IAsyncDisposable
    {
        private readonly StageLogApiTestApplicationFactory _factory;

        private readonly IReadOnlyDictionary<string, string?> _previousEnvironment;

        private StageLogApiTestHost(
            StageLogApiTestApplicationFactory factory,
            HttpClient client,
            IReadOnlyDictionary<string, string?> previousEnvironment)
        {
            _factory = factory;
            Client = client;
            _previousEnvironment = previousEnvironment;
        }

        public HttpClient Client { get; }

        public static StageLogApiTestHost Create()
        {
            var factory = new StageLogApiTestApplicationFactory();
            var previousEnvironment = ApplyEnvironmentOverrides(new Dictionary<string, string?>
            {
                ["App__StorageRoot"] = factory.StorageRootPath,
                ["App__ScheduleServiceEnabled"] = "false",
                ["App__AutomationToolCleanupEnabled"] = "false",
                ["App__CleanupIntervalMinutes"] = "0",
                ["App__UbaRemoteAgentEnabled"] = "true",
                ["App__UbaPublicHost"] = "192.168.10.20",
                ["App__UbaPort"] = "1345"
            });

            var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });

            return new StageLogApiTestHost(factory, client, previousEnvironment);
        }

        public async Task<BuildRecord> SeedBuildAsync(
            BuildStatus buildStatus,
            bool includeStageLogs,
            bool includeArtifact)
        {
            using var scope = _factory.Services.CreateScope();
            var storagePaths = scope.ServiceProvider.GetRequiredService<StoragePaths>();
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<BuildDbContext>>();

            var projectId = Guid.NewGuid();
            var buildId = Guid.NewGuid();
            var projectRoot = Path.Combine(_factory.StorageRootPath, "projects", projectId.ToString("N"));
            var buildRoot = storagePaths.ResolveBuildRoot(buildId);
            Directory.CreateDirectory(projectRoot);
            Directory.CreateDirectory(buildRoot);

            var project = new ProjectConfig
            {
                Id = projectId,
                ProjectKey = $"project-{projectId:N}",
                ProjectFingerprint = $"fingerprint-{projectId:N}",
                Name = "API Test Project",
                WorkingCopyPath = Path.Combine(projectRoot, "wc"),
                UProjectPath = Path.Combine(projectRoot, "Project.uproject"),
                EngineRootPath = Path.Combine(projectRoot, "Engine"),
                ArchiveRootPath = Path.Combine(projectRoot, "Archive")
            };

            Directory.CreateDirectory(project.WorkingCopyPath);
            Directory.CreateDirectory(project.EngineRootPath);
            Directory.CreateDirectory(project.ArchiveRootPath);

            var now = DateTimeOffset.UtcNow;
            var build = new BuildRecord
            {
                Id = buildId,
                ProjectId = project.Id,
                Project = project,
                Revision = "HEAD",
                TriggerSource = BuildTriggerSource.Manual,
                Platform = BuildPlatform.Windows,
                TargetType = BuildTargetType.Game,
                TargetName = "ApiTestTarget",
                BuildConfiguration = "Development",
                Status = buildStatus,
                CurrentPhase = buildStatus == BuildStatus.Running ? BuildPhase.Build : BuildPhase.Completed,
                ProgressPercent = buildStatus == BuildStatus.Running ? 60 : 100,
                StatusMessage = buildStatus == BuildStatus.Running ? "Running" : "Finished",
                QueuedAtUtc = now.AddMinutes(-5),
                StartedAtUtc = now.AddMinutes(-4),
                FinishedAtUtc = buildStatus == BuildStatus.Running ? null : now,
                LogFilePath = storagePaths.ResolveLogPath(buildId),
                BuildRootPath = buildRoot,
                ArchiveDirectoryPath = Path.Combine(buildRoot, "archive"),
                DownloadUrl = buildStatus == BuildStatus.Succeeded ? $"/api/builds/{buildId}/download" : null,
                ZipFilePath = string.Empty
            };

            await using var db = await dbFactory.CreateDbContextAsync();
            db.Projects.Add(project);
            db.Builds.Add(build);
            await db.SaveChangesAsync();

            await File.WriteAllTextAsync(build.LogFilePath, "build log" + Environment.NewLine, new UTF8Encoding(false));

            if (includeStageLogs)
            {
                await SeedStageLogsAsync(storagePaths, build, includeArtifact);
            }

            return build;
        }

        public async Task SeedSucceededBuildsAsync(Guid projectId, int count, DateTimeOffset newerThanUtc)
        {
            using var scope = _factory.Services.CreateScope();
            var storagePaths = scope.ServiceProvider.GetRequiredService<StoragePaths>();
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<BuildDbContext>>();

            await using var db = await dbFactory.CreateDbContextAsync();
            var project = await db.Projects.FirstAsync(item => item.Id == projectId);

            for (var index = 0; index < count; index++)
            {
                var buildId = Guid.NewGuid();
                var finishedAtUtc = newerThanUtc.AddMinutes(index + 1);
                var buildRoot = storagePaths.ResolveBuildRoot(buildId);
                Directory.CreateDirectory(buildRoot);
                var zipPath = Path.Combine(buildRoot, $"newer-{index}.zip");
                await File.WriteAllBytesAsync(zipPath, [0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

                db.Builds.Add(new BuildRecord
                {
                    Id = buildId,
                    ProjectId = project.Id,
                    Project = project,
                    Revision = "HEAD",
                    TriggerSource = BuildTriggerSource.Manual,
                    Platform = BuildPlatform.Windows,
                    TargetType = BuildTargetType.Game,
                    TargetName = "ApiTestTarget",
                    BuildConfiguration = "Development",
                    Status = BuildStatus.Succeeded,
                    CurrentPhase = BuildPhase.Completed,
                    ProgressPercent = 100,
                    StatusMessage = "Finished",
                    QueuedAtUtc = finishedAtUtc.AddMinutes(-5),
                    StartedAtUtc = finishedAtUtc.AddMinutes(-4),
                    FinishedAtUtc = finishedAtUtc,
                    LogFilePath = storagePaths.ResolveLogPath(buildId),
                    BuildRootPath = buildRoot,
                    ArchiveDirectoryPath = Path.Combine(buildRoot, "archive"),
                    DownloadUrl = $"/api/builds/{buildId}/download",
                    ZipFilePath = zipPath
                });
            }

            await db.SaveChangesAsync();
        }

        public ValueTask DisposeAsync()
        {
            Client.Dispose();
            _factory.Dispose();
            RestoreEnvironment(_previousEnvironment);
            return ValueTask.CompletedTask;
        }

        private static IReadOnlyDictionary<string, string?> ApplyEnvironmentOverrides(IReadOnlyDictionary<string, string?> overrides)
        {
            var previous = overrides.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
            foreach (var pair in overrides)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }

            return previous;
        }

        private static void RestoreEnvironment(IReadOnlyDictionary<string, string?> previous)
        {
            foreach (var pair in previous)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }

        private static async Task SeedStageLogsAsync(StoragePaths storagePaths, BuildRecord build, bool includeArtifact)
        {
            var stageLogsRoot = storagePaths.ResolveStageLogsRoot(build.BuildRootPath);
            var stageDirectory = Path.Combine(stageLogsRoot, "build");
            var artifactsDirectory = Path.Combine(stageDirectory, "artifacts");
            Directory.CreateDirectory(artifactsDirectory);

            var logPath = Path.Combine(stageDirectory, "output.log");
            var commandPath = Path.Combine(stageDirectory, "command.txt");
            await File.WriteAllTextAsync(
                logPath,
                string.Join(Environment.NewLine, ["compile line 1", "compile line 2"]) + Environment.NewLine,
                new UTF8Encoding(false));
            await File.WriteAllTextAsync(
                commandPath,
                "cmd.exe /c RunUAT.bat BuildCookRun" + Environment.NewLine,
                new UTF8Encoding(false));

            var manifest = new BuildStageLogService.BuildStageLogManifest
            {
                Stages =
                [
                    new BuildStageLogService.BuildStageLogManifestEntry
                    {
                        StageKey = "build",
                        Kind = BuildStageLogKind.Build,
                        DisplayName = "Build",
                        ParentStageKey = null,
                        Order = 1,
                        Status = build.Status == BuildStatus.Running
                            ? BuildStageLogStatus.Running
                            : BuildStageLogStatus.Completed,
                        StartedAtUtc = build.StartedAtUtc ?? build.QueuedAtUtc,
                        FinishedAtUtc = build.Status == BuildStatus.Running ? null : build.FinishedAtUtc,
                        LogRelativePath = "build/output.log",
                        LineCount = 2,
                        CommandRelativePath = "build/command.txt",
                        EnvironmentRelativePath = null,
                        InputArtifacts = new List<BuildStageLogService.BuildStageLogArtifactEntry>()
                    }
                ]
            };

            if (includeArtifact)
            {
                var artifactPath = Path.Combine(artifactsDirectory, "tool.log");
                await File.WriteAllTextAsync(artifactPath, "tool log", new UTF8Encoding(false));
                var sizeBytes = new FileInfo(artifactPath).Length;
                manifest.Stages[0].InputArtifacts.Add(new BuildStageLogService.BuildStageLogArtifactEntry
                {
                    ArtifactKey = "tool-log-01",
                    Label = "Tool log",
                    Category = "tool-log",
                    FileName = "tool.log",
                    SizeBytes = sizeBytes,
                    RelativePath = "build/artifacts/tool.log"
                });
            }

            var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true
            };
            jsonOptions.Converters.Add(new JsonStringEnumConverter());

            var manifestPath = storagePaths.ResolveStageLogManifestPath(build.BuildRootPath);
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(manifest, jsonOptions),
                new UTF8Encoding(false));
        }
    }

    private sealed class StageLogApiTestApplicationFactory : WebApplicationFactory<Program>
    {
        public StageLogApiTestApplicationFactory()
        {
            StorageRootPath = Path.Combine(
                Path.GetTempPath(),
                "backend-stage-api-tests",
                Guid.NewGuid().ToString("N"));
        }

        public string StorageRootPath { get; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["App:StorageRoot"] = StorageRootPath,
                    ["App:ScheduleServiceEnabled"] = "false",
                    ["App:AutomationToolCleanupEnabled"] = "false",
                    ["App:CleanupIntervalMinutes"] = "0",
                    ["App:UbaRemoteAgentEnabled"] = "true",
                    ["App:UbaPublicHost"] = "192.168.10.20",
                    ["App:UbaPort"] = "1345"
                });
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (!disposing)
            {
                return;
            }

            try
            {
                if (Directory.Exists(StorageRootPath))
                {
                    Directory.Delete(StorageRootPath, recursive: true);
                }
            }
            catch
            {
                // Ignore temp cleanup failures on Windows.
            }
        }
    }
}
