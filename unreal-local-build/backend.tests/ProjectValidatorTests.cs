using Backend.Contracts;
using Backend.Data;
using Backend.Models;
using Backend.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Backend.Tests;

public sealed class ProjectValidatorTests
{
    [Fact]
    public async Task ValidateBuildRequestAsync_RejectsProjectDefaultUbtArgs()
    {
        var root = Path.Combine(Path.GetTempPath(), "backend-project-validator-tests", Guid.NewGuid().ToString("N"));
        var workingCopy = Path.Combine(root, "WorkingCopy");
        var engineRoot = Path.Combine(root, "EngineRoot");
        var archiveRoot = Path.Combine(root, "Archive");
        var sourceRoot = Path.Combine(workingCopy, "Source");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(Path.Combine(engineRoot, "Engine", "Build", "BatchFiles"));
        Directory.CreateDirectory(archiveRoot);
        File.WriteAllText(Path.Combine(engineRoot, "Engine", "Build", "BatchFiles", "RunUAT.bat"), string.Empty);
        File.WriteAllText(Path.Combine(workingCopy, "Test.uproject"), "{}");
        File.WriteAllText(Path.Combine(sourceRoot, "TestGame.Target.cs"), string.Empty);

        var project = new ProjectConfig
        {
            Id = Guid.NewGuid(),
            ProjectKey = "test-project",
            ProjectFingerprint = "test-fingerprint",
            Name = "TestProject",
            WorkingCopyPath = workingCopy,
            UProjectPath = Path.Combine(workingCopy, "Test.uproject"),
            EngineRootPath = engineRoot,
            ArchiveRootPath = archiveRoot,
            GameTarget = "TestGame",
            AllowedBuildConfigurations = ["Development"],
            DefaultExtraUatArgs = ["-ubtargs=-NoHotReload"]
        };

        await using var dbFactory = new SqliteMemoryBuildDbContextFactory();
        var validator = new ProjectValidator(dbFactory, NullLogger<ProjectValidator>.Instance);
        var request = new QueueBuildRequest(
            project.Id,
            "HEAD",
            BuildPlatform.Windows,
            BuildTargetType.Game,
            "Development",
            BuildAccelerator.Uba,
            AndroidPackagingMode.ExternalFilesIoStore,
            Clean: false,
            Pak: true,
            IoStore: true,
            ExtraUatArgs: null);

        var errors = await validator.ValidateBuildRequestAsync(project, request, CancellationToken.None);

        Assert.True(errors.ContainsKey(nameof(QueueBuildRequest.ExtraUatArgs)));
    }

    [Fact]
    public async Task ValidateBuildRequestAsync_RejectsAndroidExternalFilesConflictingExtraArgs()
    {
        var root = Path.Combine(Path.GetTempPath(), "backend-project-validator-tests", Guid.NewGuid().ToString("N"));
        var workingCopy = Path.Combine(root, "WorkingCopy");
        var engineRoot = Path.Combine(root, "EngineRoot");
        var archiveRoot = Path.Combine(root, "Archive");
        var sourceRoot = Path.Combine(workingCopy, "Source");
        var configRoot = Path.Combine(workingCopy, "Config");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(configRoot);
        Directory.CreateDirectory(Path.Combine(engineRoot, "Engine", "Build", "BatchFiles"));
        Directory.CreateDirectory(archiveRoot);
        File.WriteAllText(Path.Combine(engineRoot, "Engine", "Build", "BatchFiles", "RunUAT.bat"), string.Empty);
        File.WriteAllText(Path.Combine(workingCopy, "Test.uproject"), "{}");
        File.WriteAllText(Path.Combine(sourceRoot, "TestGame.Target.cs"), string.Empty);
        File.WriteAllText(
            Path.Combine(configRoot, "DefaultEngine.ini"),
            "[/Script/AndroidRuntimeSettings.AndroidRuntimeSettings]");

        var project = new ProjectConfig
        {
            Id = Guid.NewGuid(),
            ProjectKey = "test-project",
            ProjectFingerprint = "test-fingerprint",
            Name = "TestProject",
            WorkingCopyPath = workingCopy,
            UProjectPath = Path.Combine(workingCopy, "Test.uproject"),
            EngineRootPath = engineRoot,
            ArchiveRootPath = archiveRoot,
            GameTarget = "TestGame",
            AndroidEnabled = true,
            AllowedBuildConfigurations = ["Development"]
        };

        await using var dbFactory = new SqliteMemoryBuildDbContextFactory();
        var validator = new ProjectValidator(dbFactory, NullLogger<ProjectValidator>.Instance);
        var request = new QueueBuildRequest(
            project.Id,
            "HEAD",
            BuildPlatform.Android,
            BuildTargetType.Game,
            "Development",
            BuildAccelerator.None,
            AndroidPackagingMode.ExternalFilesIoStore,
            Clean: false,
            Pak: true,
            IoStore: true,
            ExtraUatArgs: ["-skipiostore"]);

        var errors = await validator.ValidateBuildRequestAsync(project, request, CancellationToken.None);

        Assert.True(errors.ContainsKey(nameof(QueueBuildRequest.ExtraUatArgs)));
    }

    private sealed class SqliteMemoryBuildDbContextFactory : IDbContextFactory<BuildDbContext>, IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<BuildDbContext> _options;

        public SqliteMemoryBuildDbContextFactory()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            _options = new DbContextOptionsBuilder<BuildDbContext>()
                .UseSqlite(_connection)
                .Options;
            using var db = new BuildDbContext(_options);
            db.Database.EnsureCreated();
        }

        public BuildDbContext CreateDbContext()
        {
            return new BuildDbContext(_options);
        }

        public ValueTask DisposeAsync()
        {
            return _connection.DisposeAsync();
        }
    }
}
