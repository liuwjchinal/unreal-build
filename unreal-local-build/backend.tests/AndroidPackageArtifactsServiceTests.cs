using System.Text;
using Backend.Models;
using Backend.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Backend.Tests;

public sealed class AndroidPackageArtifactsServiceTests
{
    [Fact]
    public async Task CreateExternalDataArtifactsAsync_CreatesAndroidLayoutManifestAndInstaller()
    {
        var root = Path.Combine(Path.GetTempPath(), "backend-android-artifacts-tests", Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(root, "Project");
        var archiveRoot = Path.Combine(root, "Archive");
        var stagedPaks = Path.Combine(projectRoot, "Saved", "StagedBuilds", "Android_ASTC", "UEStarterGame", "Content", "Paks");
        Directory.CreateDirectory(stagedPaks);
        Directory.CreateDirectory(Path.Combine(archiveRoot, "Android_ASTC"));

        var apkPath = Path.Combine(archiveRoot, "Android_ASTC", "UEStarterGame-arm64.apk");
        var obbPath = Path.Combine(archiveRoot, "Android_ASTC", "main.1.com.example.uestartergame.obb");
        var installBatPath = Path.Combine(archiveRoot, "Android_ASTC", "Install_UEStarterGame-arm64.bat");
        await File.WriteAllTextAsync(apkPath, "apk", new UTF8Encoding(false));
        await File.WriteAllTextAsync(obbPath, "obb", new UTF8Encoding(false));
        await File.WriteAllTextAsync(
            installBatPath,
            "set PACKAGE=com.example.uestartergame" + Environment.NewLine,
            new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(stagedPaks, "pakchunk0-Android_ASTC.pak"), "pak", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(stagedPaks, "pakchunk0-Android_ASTC.utoc"), "utoc", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(stagedPaks, "pakchunk0-Android_ASTC.ucas"), "ucas", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(stagedPaks, "pakchunk0-Android_ASTC.sig"), "sig", new UTF8Encoding(false));

        var project = new ProjectConfig
        {
            Id = Guid.NewGuid(),
            ProjectKey = "starter",
            ProjectFingerprint = "starter",
            Name = "StarterGame",
            WorkingCopyPath = projectRoot,
            UProjectPath = Path.Combine(projectRoot, "UEStarterGame.uproject"),
            EngineRootPath = Path.Combine(root, "Engine"),
            ArchiveRootPath = archiveRoot,
            GameTarget = "UEStarterGame",
            AndroidTextureFlavor = "ASTC"
        };
        var build = new BuildRecord
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Project = project,
            Revision = "13227",
            Platform = BuildPlatform.Android,
            TargetType = BuildTargetType.Game,
            TargetName = "UEStarterGame",
            BuildConfiguration = "Development",
            AndroidPackagingMode = AndroidPackagingMode.ExternalFilesIoStore,
            ArchiveDirectoryPath = archiveRoot
        };

        var service = new AndroidPackageArtifactsService(NullLogger<AndroidPackageArtifactsService>.Instance);
        using var log = new StringWriter();

        var manifest = await service.CreateExternalDataArtifactsAsync(project, build, log, CancellationToken.None);

        Assert.NotNull(manifest);
        Assert.Equal("UEStarterGame", manifest!.ProjectName);
        Assert.Equal("com.example.uestartergame", manifest.PackageName);
        Assert.Equal("Android/apk/UEStarterGame-arm64.apk", NormalizeArchivePath(archiveRoot, Path.Combine(archiveRoot, "Android", manifest.ApkPath)));
        Assert.Equal("data/UEStarterGame/Content/Paks", manifest.DataRoot);
        Assert.Equal(4, manifest.Files.Count);
        Assert.True(manifest.TotalDataSizeBytes > 0);
        Assert.False(File.Exists(obbPath));
        Assert.True(File.Exists(Path.Combine(archiveRoot, "Android", "apk", "UEStarterGame-arm64.apk")));
        Assert.True(File.Exists(Path.Combine(archiveRoot, "Android", "data", "UEStarterGame", "Content", "Paks", "pakchunk0-Android_ASTC.ucas")));
        Assert.True(File.Exists(Path.Combine(archiveRoot, "Android", AndroidPackageArtifactsService.InstallerFileName)));
        Assert.True(File.Exists(Path.Combine(archiveRoot, "Android", AndroidPackageArtifactsService.ManifestFileName)));
        Assert.False(Directory.Exists(Path.Combine(archiveRoot, "Android_ASTC")));
        Assert.Equal(Path.Combine(archiveRoot, "Android", AndroidPackageArtifactsService.ManifestFileName), build.AndroidPackageManifestPath);
        Assert.Equal(Path.Combine(archiveRoot, "Android", AndroidPackageArtifactsService.InstallerFileName), build.AndroidInstallScriptPath);

        Assert.NotNull(build.AndroidInstallScriptPath);
        var script = await File.ReadAllTextAsync(build.AndroidInstallScriptPath!);
        Assert.Contains("/sdcard/Android/data/$PackageName/files/UnrealGame/$ProjectName/$ProjectName/Content/Paks", script, StringComparison.Ordinal);
        Assert.Contains("--clean-data", script, StringComparison.Ordinal);
        Assert.Contains("--launch", script, StringComparison.Ordinal);

        var loaded = AndroidPackageArtifactsService.TryReadManifest(build.AndroidPackageManifestPath);
        Assert.NotNull(loaded);
        Assert.Equal(manifest.PackageName, loaded!.PackageName);
    }

    private static string NormalizeArchivePath(string archiveRoot, string path)
    {
        return Path.GetRelativePath(archiveRoot, path).Replace('\\', '/');
    }
}
