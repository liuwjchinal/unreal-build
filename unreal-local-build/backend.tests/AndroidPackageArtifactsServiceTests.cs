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
        var stagedRoot = Path.Combine(projectRoot, "Saved", "StagedBuilds", "Android_ASTC");
        var stagedPaks = Path.Combine(projectRoot, "Saved", "StagedBuilds", "Android_ASTC", "UEStarterGame", "Content", "Paks");
        Directory.CreateDirectory(stagedPaks);
        Directory.CreateDirectory(Path.Combine(archiveRoot, "Android_ASTC"));
        var stagedLooseFile = Path.Combine(stagedRoot, "Engine", "Content", "Renderer", "TessellationTable.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(stagedLooseFile)!);

        var apkPath = Path.Combine(archiveRoot, "Android_ASTC", "UEStarterGame-arm64.apk");
        var obbPath = Path.Combine(archiveRoot, "Android_ASTC", "main.1.com.example.uestartergame.obb");
        var installBatPath = Path.Combine(archiveRoot, "Android_ASTC", "Install_UEStarterGame-arm64.bat");
        await File.WriteAllTextAsync(apkPath, "apk", new UTF8Encoding(false));
        await File.WriteAllTextAsync(obbPath, "obb", new UTF8Encoding(false));
        await File.WriteAllTextAsync(
            installBatPath,
            "set PACKAGE=com.example.uestartergame" + Environment.NewLine +
            "echo com.epicgames.unreal.GameActivity" + Environment.NewLine,
            new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(stagedPaks, "pakchunk0-Android_ASTC.pak"), "pak", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(stagedPaks, "pakchunk0-Android_ASTC.utoc"), "utoc", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(stagedPaks, "pakchunk0-Android_ASTC.ucas"), "ucas", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(stagedPaks, "pakchunk0-Android_ASTC.sig"), "sig", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(stagedPaks, "pakchunk101-Android_ASTC_0.ucas"), "map-ucas", new UTF8Encoding(false));
        await File.WriteAllTextAsync(stagedLooseFile, "loose", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(stagedRoot, "Manifest_UFSFiles_Android.txt"), "manifest", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(stagedRoot, "UECommandLine.txt"), "cmd", new UTF8Encoding(false));

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
        Assert.Equal("data/UEStarterGame", manifest.DataRoot);
        Assert.Equal(6, manifest.Files.Count);
        Assert.Equal(2, manifest.Chunks.Count);
        Assert.Contains(manifest.Files, file =>
            file.FileName == "pakchunk101-Android_ASTC_0.ucas" &&
            file.StageRelativePath == "UEStarterGame/Content/Paks/pakchunk101-Android_ASTC_0.ucas" &&
            file.IsContainer &&
            file.ChunkId == 101 &&
            file.ChunkName == "chunk101");
        Assert.Contains(manifest.Files, file =>
            file.FileName == "TessellationTable.bin" &&
            file.StageRelativePath == "Engine/Content/Renderer/TessellationTable.bin" &&
            !file.IsContainer &&
            file.ChunkId is null);
        var chunk0 = Assert.Single(manifest.Chunks, chunk => chunk.ChunkId == 0);
        Assert.Equal("chunk0", chunk0.ChunkName);
        Assert.Equal(4, chunk0.FileCount);
        var chunk101 = Assert.Single(manifest.Chunks, chunk => chunk.ChunkId == 101);
        Assert.Equal("chunk101", chunk101.ChunkName);
        Assert.Equal(1, chunk101.FileCount);
        Assert.True(manifest.TotalDataSizeBytes > 0);
        Assert.False(File.Exists(obbPath));
        Assert.True(File.Exists(Path.Combine(archiveRoot, "Android", "apk", "UEStarterGame-arm64.apk")));
        Assert.True(File.Exists(Path.Combine(archiveRoot, "Android", "data", "UEStarterGame", "UEStarterGame", "Content", "Paks", "pakchunk0-Android_ASTC.ucas")));
        Assert.True(File.Exists(Path.Combine(archiveRoot, "Android", "data", "UEStarterGame", "Engine", "Content", "Renderer", "TessellationTable.bin")));
        Assert.False(File.Exists(Path.Combine(archiveRoot, "Android", "data", "UEStarterGame", "Manifest_UFSFiles_Android.txt")));
        Assert.False(File.Exists(Path.Combine(archiveRoot, "Android", "data", "UEStarterGame", "UECommandLine.txt")));
        Assert.True(File.Exists(Path.Combine(archiveRoot, "Android", AndroidPackageArtifactsService.InstallerFileName)));
        Assert.True(File.Exists(Path.Combine(archiveRoot, "Android", AndroidPackageArtifactsService.ManifestFileName)));
        var generatedInstallBatPath = Path.Combine(archiveRoot, "Android", "Install_UEStarterGame-arm64.bat");
        var generatedUninstallBatPath = Path.Combine(archiveRoot, "Android", "Uninstall_UEStarterGame-arm64.bat");
        Assert.True(File.Exists(generatedInstallBatPath));
        Assert.True(File.Exists(generatedUninstallBatPath));
        Assert.False(Directory.Exists(Path.Combine(archiveRoot, "Android_ASTC")));
        Assert.Equal(Path.Combine(archiveRoot, "Android", AndroidPackageArtifactsService.ManifestFileName), build.AndroidPackageManifestPath);
        Assert.Equal(Path.Combine(archiveRoot, "Android", AndroidPackageArtifactsService.InstallerFileName), build.AndroidInstallScriptPath);

        Assert.NotNull(build.AndroidInstallScriptPath);
        var script = await File.ReadAllTextAsync(build.AndroidInstallScriptPath!);
        Assert.Contains("$RemoteRoot = \"/sdcard/Android/data/$PackageName/files/UnrealGame/$ProjectName\"", script, StringComparison.Ordinal);
        Assert.Contains("$RemoteContainerDir = \"$RemoteRoot/$ProjectName/Content/Paks\"", script, StringComparison.Ordinal);
        Assert.Contains("--clean-data", script, StringComparison.Ordinal);
        Assert.Contains("--launch", script, StringComparison.Ordinal);
        Assert.Contains("--no-prune-stale", script, StringComparison.Ordinal);
        Assert.Contains("--chunks", script, StringComparison.Ordinal);
        Assert.Contains("ConvertTo-ChunkIdSet $ChunkFilter", script, StringComparison.Ordinal);
        Assert.Contains("$SelectedContainers = @($Files | Where-Object { $_.IsContainer -and $null -ne $_.ChunkId -and $SelectedChunkIds.ContainsKey([int]$_.ChunkId) })", script, StringComparison.Ordinal);
        Assert.Contains("$SelectedFiles = @($Files | Where-Object { !$_.IsContainer }) + $SelectedContainers", script, StringComparison.Ordinal);
        Assert.Contains("StagePath = 'Engine/Content/Renderer/TessellationTable.bin'; IsContainer = $false", script, StringComparison.Ordinal);
        Assert.Contains("--clean-data cannot be combined with --chunks", script, StringComparison.Ordinal);
        Assert.Contains("Get-ContainerChunkIdFromFileName $remoteName", script, StringComparison.Ordinal);
        Assert.Contains("Stale remote cleanup is scoped to selected chunks.", script, StringComparison.Ordinal);
        Assert.Contains("No container files matched --chunks $ChunkFilter", script, StringComparison.Ordinal);
        Assert.Contains("Test-InstalledPackage $PackageName", script, StringComparison.Ordinal);
        Assert.Contains("pm path $quotedPackage", script, StringComparison.Ordinal);
        Assert.Contains("wc -c < $quotedPath", script, StringComparison.Ordinal);
        Assert.Contains("toybox stat -c %s $quotedPath", script, StringComparison.Ordinal);
        Assert.Contains("ls -ln $quotedPath", script, StringComparison.Ordinal);
        Assert.Contains("Get-RemoteContainerFiles $RemoteContainerDir", script, StringComparison.Ordinal);
        Assert.Contains("REMOVE stale $remoteName", script, StringComparison.Ordinal);
        Assert.Contains("Skipping stale remote container cleanup.", script, StringComparison.Ordinal);
        Assert.Contains("Remote file size mismatch after push", script, StringComparison.Ordinal);
        Assert.Contains("removed {2}", script, StringComparison.Ordinal);

        var generatedInstallBat = await File.ReadAllTextAsync(generatedInstallBatPath);
        Assert.Contains(@"set ""ROOT=%~dp0""", generatedInstallBat, StringComparison.Ordinal);
        Assert.Contains(@"set ""APK_PATH=%ROOT%apk\UEStarterGame-arm64.apk""", generatedInstallBat, StringComparison.Ordinal);
        Assert.Contains(@"set ""INSTALL_PS1=%ROOT%install-android-external-data.ps1""", generatedInstallBat, StringComparison.Ordinal);
        Assert.Contains(@"powershell.exe -NoProfile -ExecutionPolicy Bypass -File ""%INSTALL_PS1%""", generatedInstallBat, StringComparison.Ordinal);
        Assert.Contains(@"if defined ANDROID_HOME if exist ""%ANDROID_HOME%\platform-tools\adb.exe""", generatedInstallBat, StringComparison.Ordinal);

        var generatedUninstallBat = await File.ReadAllTextAsync(generatedUninstallBatPath);
        Assert.Contains(@"set ""PACKAGE_NAME=com.example.uestartergame""", generatedUninstallBat, StringComparison.Ordinal);
        Assert.Contains(@"""%ADB%"" uninstall %PACKAGE_NAME%", generatedUninstallBat, StringComparison.Ordinal);
        Assert.Contains(@"if defined ANDROID_SDK_ROOT if exist ""%ANDROID_SDK_ROOT%\platform-tools\adb.exe""", generatedUninstallBat, StringComparison.Ordinal);

        var loaded = AndroidPackageArtifactsService.TryReadManifest(build.AndroidPackageManifestPath);
        Assert.NotNull(loaded);
        Assert.Equal(manifest.PackageName, loaded!.PackageName);
        Assert.Equal(2, loaded.Chunks.Count);
    }

    [Fact]
    public async Task CreateExternalDataArtifactsAsync_UsesPackageInfo_WhenInstallScriptIsMissing()
    {
        var context = await CreateMinimalExternalDataSetupAsync(packageInfoPackageName: "com.example.frompackageinfo");
        var service = new AndroidPackageArtifactsService(NullLogger<AndroidPackageArtifactsService>.Instance);
        using var log = new StringWriter();

        var manifest = await service.CreateExternalDataArtifactsAsync(context.Project, context.Build, log, CancellationToken.None);

        Assert.NotNull(manifest);
        Assert.Equal("com.example.frompackageinfo", manifest!.PackageName);
    }

    [Fact]
    public async Task CreateExternalDataArtifactsAsync_UsesBuildLog_WhenPackageInfoIsMissing()
    {
        var context = await CreateMinimalExternalDataSetupAsync(
            buildLogText: "[2026.06.23] Using package name: 'com.example.fromlog'");
        var service = new AndroidPackageArtifactsService(NullLogger<AndroidPackageArtifactsService>.Instance);
        using var log = new StringWriter();

        var manifest = await service.CreateExternalDataArtifactsAsync(context.Project, context.Build, log, CancellationToken.None);

        Assert.NotNull(manifest);
        Assert.Equal("com.example.fromlog", manifest!.PackageName);
    }

    [Fact]
    public async Task CreateExternalDataArtifactsAsync_Throws_WhenPackageNameCannotBeResolved()
    {
        var context = await CreateMinimalExternalDataSetupAsync();
        var service = new AndroidPackageArtifactsService(NullLogger<AndroidPackageArtifactsService>.Instance);
        using var log = new StringWriter();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateExternalDataArtifactsAsync(context.Project, context.Build, log, CancellationToken.None));

        Assert.Contains("Unable to resolve the Android package name", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateExternalDataArtifactsAsync_Throws_WhenStagedProjectNameIsUnsafe()
    {
        var context = await CreateMinimalExternalDataSetupAsync(
            packageInfoPackageName: "com.example.unsafe",
            stagedProjectName: "Unsafe Project");
        var service = new AndroidPackageArtifactsService(NullLogger<AndroidPackageArtifactsService>.Instance);
        using var log = new StringWriter();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateExternalDataArtifactsAsync(context.Project, context.Build, log, CancellationToken.None));

        Assert.Contains("not safe for the ExternalFilesDir data path", exception.Message, StringComparison.Ordinal);
    }

    private static async Task<AndroidArtifactTestContext> CreateMinimalExternalDataSetupAsync(
        string? packageInfoPackageName = null,
        string? buildLogText = null,
        string stagedProjectName = "UEStarterGame")
    {
        var root = Path.Combine(Path.GetTempPath(), "backend-android-artifacts-tests", Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(root, "Project");
        var archiveRoot = Path.Combine(root, "Archive");
        var stagedPaks = Path.Combine(projectRoot, "Saved", "StagedBuilds", "Android_ASTC", stagedProjectName, "Content", "Paks");
        Directory.CreateDirectory(stagedPaks);
        Directory.CreateDirectory(Path.Combine(archiveRoot, "Android_ASTC"));

        await File.WriteAllTextAsync(
            Path.Combine(archiveRoot, "Android_ASTC", "UEStarterGame-arm64.apk"),
            "apk",
            new UTF8Encoding(false));
        await File.WriteAllTextAsync(
            Path.Combine(stagedPaks, "pakchunk0-Android_ASTC.ucas"),
            "ucas",
            new UTF8Encoding(false));

        if (!string.IsNullOrWhiteSpace(packageInfoPackageName))
        {
            var packageInfoDirectory = Path.Combine(projectRoot, "Binaries", "Android");
            Directory.CreateDirectory(packageInfoDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(packageInfoDirectory, "packageInfo.txt"),
                packageInfoPackageName + Environment.NewLine + "1" + Environment.NewLine,
                new UTF8Encoding(false));
        }

        var logFilePath = string.Empty;
        if (!string.IsNullOrWhiteSpace(buildLogText))
        {
            logFilePath = Path.Combine(root, "build.log");
            await File.WriteAllTextAsync(logFilePath, buildLogText, new UTF8Encoding(false));
        }

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
            ArchiveDirectoryPath = archiveRoot,
            LogFilePath = logFilePath
        };

        return new AndroidArtifactTestContext(project, build);
    }

    private static string NormalizeArchivePath(string archiveRoot, string path)
    {
        return Path.GetRelativePath(archiveRoot, path).Replace('\\', '/');
    }

    private sealed record AndroidArtifactTestContext(ProjectConfig Project, BuildRecord Build);
}
