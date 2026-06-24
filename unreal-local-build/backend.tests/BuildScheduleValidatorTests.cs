using Backend.Contracts;
using Backend.Data;
using Backend.Models;
using Backend.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Backend.Tests;

public sealed class BuildScheduleValidatorTests
{
    [Fact]
    public async Task ValidateAsync_RejectsProjectDefaultAndroidExternalFilesConflictingExtraArgs()
    {
        await using var dbFactory = new SqliteMemoryBuildDbContextFactory();
        using (var db = dbFactory.CreateDbContext())
        {
            db.Projects.Add(new ProjectConfig
            {
                Id = TestProjectId,
                ProjectKey = "test-project",
                ProjectFingerprint = "test-fingerprint",
                Name = "TestProject",
                AndroidEnabled = true,
                GameTarget = "TestGame",
                AllowedBuildConfigurations = ["Development"],
                DefaultExtraUatArgs = ["-forcepackagedata"]
            });
            await db.SaveChangesAsync();
        }

        var validator = new BuildScheduleValidator(dbFactory);
        var request = new UpsertBuildScheduleRequest(
            "Android nightly",
            Enabled: true,
            BuildScheduleScopeType.SingleProject,
            TestProjectId,
            "12:00",
            BuildPlatform.Android,
            BuildTargetType.Game,
            "Development",
            BuildAccelerator.None,
            AndroidPackagingMode.ExternalFilesIoStore,
            Clean: false,
            Pak: true,
            IoStore: true,
            ExtraUatArgs: null);

        var errors = await validator.ValidateAsync(request, existingScheduleId: null, CancellationToken.None);

        Assert.True(errors.ContainsKey(nameof(UpsertBuildScheduleRequest.ExtraUatArgs)));
    }

    [Fact]
    public async Task ValidateAsync_AllowsMatchingAndroidExternalFilesArgs()
    {
        await using var dbFactory = new SqliteMemoryBuildDbContextFactory();
        using (var db = dbFactory.CreateDbContext())
        {
            db.Projects.Add(new ProjectConfig
            {
                Id = TestProjectId,
                ProjectKey = "test-project",
                ProjectFingerprint = "test-fingerprint",
                Name = "TestProject",
                AndroidEnabled = true,
                GameTarget = "TestGame",
                AllowedBuildConfigurations = ["Development"],
                DefaultExtraUatArgs = ["-ini:Engine:[/Script/AndroidRuntimeSettings.AndroidRuntimeSettings]:bPackageDataInsideApk=False"]
            });
            await db.SaveChangesAsync();
        }

        var validator = new BuildScheduleValidator(dbFactory);
        var request = new UpsertBuildScheduleRequest(
            "Android nightly",
            Enabled: true,
            BuildScheduleScopeType.SingleProject,
            TestProjectId,
            "12:00",
            BuildPlatform.Android,
            BuildTargetType.Game,
            "Development",
            BuildAccelerator.None,
            AndroidPackagingMode.ExternalFilesIoStore,
            Clean: false,
            Pak: true,
            IoStore: true,
            ExtraUatArgs: ["-ini:Engine:[/Script/AndroidRuntimeSettings.AndroidRuntimeSettings]:bUseExternalFilesDir=True"]);

        var errors = await validator.ValidateAsync(request, existingScheduleId: null, CancellationToken.None);

        Assert.False(errors.ContainsKey(nameof(UpsertBuildScheduleRequest.ExtraUatArgs)));
    }

    private static readonly Guid TestProjectId = Guid.NewGuid();

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
