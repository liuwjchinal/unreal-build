using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Diagnostics;
using Backend.Models;

namespace Backend.Services;

public sealed class AndroidPackageArtifactsService(ILogger<AndroidPackageArtifactsService> logger)
{
    public const string AndroidDirectoryName = "Android";
    public const string InstallerFileName = "install-android-external-data.ps1";
    public const string ManifestFileName = "android-package-manifest.json";

    private static readonly string[] ContainerExtensions = [".pak", ".utoc", ".ucas", ".sig"];
    private static readonly JsonSerializerOptions ManifestJsonOptions = CreateManifestJsonOptions();
    private static readonly Regex PackageNameFromObbRegex = new(@"^(?:main|patch|overflow\d+)\.\d+\.(?<package>.+)\.obb$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ExplicitPackageNameRegex = new(@"(?:set\s+)?(?:package|packagename)\s*=\s*(?<package>[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z][A-Za-z0-9_]*){2,})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PackageNameFromInstallScriptRegex = new(@"(?<package>[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z][A-Za-z0-9_]*){2,})", RegexOptions.Compiled);

    public async Task<AndroidPackageArtifactManifest?> CreateExternalDataArtifactsAsync(
        ProjectConfig project,
        BuildRecord build,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        if (build.Platform != BuildPlatform.Android ||
            build.AndroidPackagingMode != AndroidPackagingMode.ExternalFilesIoStore)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(build.ArchiveDirectoryPath) || !Directory.Exists(build.ArchiveDirectoryPath))
        {
            throw new DirectoryNotFoundException($"Android archive directory does not exist: {build.ArchiveDirectoryPath}");
        }

        var apkPath = LocateApk(build.ArchiveDirectoryPath);
        if (apkPath is null)
        {
            throw new FileNotFoundException($"No Android APK was found under {build.ArchiveDirectoryPath}.");
        }

        var pakDirectory = LocateStagedPakDirectory(project, build);
        if (pakDirectory is null)
        {
            throw new DirectoryNotFoundException($"No staged Android Content/Paks directory was found for project {project.Name}.");
        }

        var stagedProjectName = ResolveStagedProjectName(pakDirectory, project);
        var packageName = ResolvePackageName(build.ArchiveDirectoryPath, apkPath, stagedProjectName);
        var containerFiles = Directory.EnumerateFiles(pakDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(IsContainerFile)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (containerFiles.Count == 0)
        {
            throw new FileNotFoundException($"No .pak/.utoc/.ucas/.sig files were found in {pakDirectory}.");
        }

        var androidRoot = Path.Combine(build.ArchiveDirectoryPath, AndroidDirectoryName);
        if (Directory.Exists(androidRoot))
        {
            Directory.Delete(androidRoot, recursive: true);
        }

        var apkOutputDirectory = Path.Combine(androidRoot, "apk");
        var dataOutputDirectory = Path.Combine(androidRoot, "data", stagedProjectName, "Content", "Paks");
        Directory.CreateDirectory(apkOutputDirectory);
        Directory.CreateDirectory(dataOutputDirectory);

        var apkOutputPath = Path.Combine(apkOutputDirectory, Path.GetFileName(apkPath));
        File.Copy(apkPath, apkOutputPath, overwrite: true);

        var manifestFiles = new List<AndroidPackageArtifactFileEntry>();
        foreach (var sourceFile in containerFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var destinationFile = Path.Combine(dataOutputDirectory, Path.GetFileName(sourceFile));
            File.Copy(sourceFile, destinationFile, overwrite: true);
            var info = new FileInfo(destinationFile);
            manifestFiles.Add(new AndroidPackageArtifactFileEntry(
                ToManifestRelativePath(androidRoot, destinationFile),
                info.Name,
                info.Length,
                info.LastWriteTimeUtc));
        }

        var manifest = new AndroidPackageArtifactManifest
        {
            ProjectName = stagedProjectName,
            PackageName = packageName,
            BuildId = build.Id,
            Revision = build.DisplayRevision,
            PackagingMode = build.AndroidPackagingMode.ToString(),
            ApkPath = ToManifestRelativePath(androidRoot, apkOutputPath),
            DataRoot = $"data/{stagedProjectName}/Content/Paks",
            ApkSizeBytes = new FileInfo(apkOutputPath).Length,
            TotalDataSizeBytes = manifestFiles.Sum(item => item.SizeBytes),
            GeneratedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            Files = manifestFiles
        };

        var installScriptPath = Path.Combine(androidRoot, InstallerFileName);
        await File.WriteAllTextAsync(
            installScriptPath,
            CreateInstallScript(manifest),
            new UTF8Encoding(false),
            cancellationToken);

        var manifestPath = Path.Combine(androidRoot, ManifestFileName);
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(manifest, ManifestJsonOptions),
            new UTF8Encoding(false),
            cancellationToken);

        PruneArchiveToAndroidRoot(build.ArchiveDirectoryPath, androidRoot);

        build.AndroidPackageManifestPath = manifestPath;
        build.AndroidInstallScriptPath = installScriptPath;

        await log.WriteLineAsync($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] Android ExternalFilesIoStore artifact prepared.");
        await log.WriteLineAsync($"  APK: {manifest.ApkPath} ({manifest.ApkSizeBytes} bytes)");
        await log.WriteLineAsync($"  Data: {manifest.DataRoot} ({manifest.Files.Count} files, {manifest.TotalDataSizeBytes} bytes)");
        await log.WriteLineAsync($"  Package: {manifest.PackageName}");

        logger.LogInformation(
            "Prepared Android external data artifacts for build {BuildId}. Package={PackageName}, Project={ProjectName}, FileCount={FileCount}, TotalDataSizeBytes={TotalDataSizeBytes}",
            build.Id,
            manifest.PackageName,
            manifest.ProjectName,
            manifest.Files.Count,
            manifest.TotalDataSizeBytes);

        return manifest;
    }

    public static AndroidPackageArtifactManifest? TryReadManifest(string? manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(manifestPath);
            return JsonSerializer.Deserialize<AndroidPackageArtifactManifest>(json, ManifestJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string? LocateApk(string archiveDirectoryPath)
    {
        return Directory.EnumerateFiles(archiveDirectoryPath, "*.apk", SearchOption.AllDirectories)
            .Where(path => !IsUnderAndroidOutput(path, archiveDirectoryPath))
            .OrderBy(path => IsPreferredApkName(path) ? 0 : 1)
            .ThenByDescending(path => new FileInfo(path).LastWriteTimeUtc)
            .ThenByDescending(path => new FileInfo(path).Length)
            .FirstOrDefault();
    }

    private static string? LocateStagedPakDirectory(ProjectConfig project, BuildRecord build)
    {
        var projectDirectory = Path.GetDirectoryName(project.UProjectPath);
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            return null;
        }

        var flavor = string.IsNullOrWhiteSpace(project.AndroidTextureFlavor)
            ? BuildCommandFactory.GetDefaultAndroidTextureFlavor()
            : project.AndroidTextureFlavor.Trim();
        var stagedRoot = Path.Combine(projectDirectory, "Saved", "StagedBuilds", $"Android_{flavor.ToUpperInvariant()}");
        if (!Directory.Exists(stagedRoot))
        {
            return null;
        }

        var targetName = build.TargetName;
        var candidates = Directory.EnumerateDirectories(stagedRoot, "Paks", SearchOption.AllDirectories)
            .Where(path => string.Equals(Path.GetFileName(path), "Paks", StringComparison.OrdinalIgnoreCase))
            .Where(path => string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "Content", StringComparison.OrdinalIgnoreCase))
            .Where(path => Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly).Any(IsContainerFile))
            .Select(path => new
            {
                Path = path,
                ProjectName = ResolveStagedProjectName(path, project),
                LastWriteUtc = Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly)
                    .Select(File.GetLastWriteTimeUtc)
                    .DefaultIfEmpty(DateTime.MinValue)
                    .Max()
            })
            .OrderByDescending(item => string.Equals(item.ProjectName, targetName, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(item => string.Equals(item.ProjectName, project.Name, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(item => item.LastWriteUtc)
            .Select(item => item.Path)
            .FirstOrDefault();

        return candidates;
    }

    private static string ResolveStagedProjectName(string pakDirectory, ProjectConfig project)
    {
        var contentDirectory = Directory.GetParent(pakDirectory);
        var stagedProjectDirectory = contentDirectory?.Parent;
        var stagedProjectName = stagedProjectDirectory?.Name;
        return string.IsNullOrWhiteSpace(stagedProjectName) ? project.Name : stagedProjectName;
    }

    private static string ResolvePackageName(string archiveDirectoryPath, string apkPath, string projectName)
    {
        var fromApk = TryReadPackageNameFromApk(apkPath);
        if (!string.IsNullOrWhiteSpace(fromApk))
        {
            return fromApk;
        }

        var fromInstallScript = Directory.EnumerateFiles(archiveDirectoryPath, "Install_*.bat", SearchOption.AllDirectories)
            .Select(TryReadPackageNameFromInstallScript)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (!string.IsNullOrWhiteSpace(fromInstallScript))
        {
            return fromInstallScript;
        }

        var fromObb = Directory.EnumerateFiles(archiveDirectoryPath, "*.obb", SearchOption.AllDirectories)
            .Select(path => PackageNameFromObbRegex.Match(Path.GetFileName(path)))
            .Where(match => match.Success)
            .Select(match => match.Groups["package"].Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (!string.IsNullOrWhiteSpace(fromObb))
        {
            return fromObb;
        }

        return $"com.YourCompany.{projectName}";
    }

    private static string? TryReadPackageNameFromApk(string apkPath)
    {
        try
        {
            var aaptPath = LocateAapt();
            if (string.IsNullOrWhiteSpace(aaptPath))
            {
                return null;
            }

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(aaptPath)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("dump");
            process.StartInfo.ArgumentList.Add("badging");
            process.StartInfo.ArgumentList.Add(apkPath);

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(milliseconds: 10000) || process.ExitCode != 0)
            {
                TryKillProcess(process);
                return null;
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            _ = stderrTask.GetAwaiter().GetResult();

            var match = Regex.Match(stdout, @"package:\s+name='(?<package>[^']+)'", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups["package"].Value : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? LocateAapt()
    {
        var sdkRoot = AndroidToolchain.ResolveSdkRoot();
        if (!string.IsNullOrWhiteSpace(sdkRoot))
        {
            var buildToolsDirectory = Path.Combine(sdkRoot, "build-tools");
            if (Directory.Exists(buildToolsDirectory))
            {
                var fromBuildTools = Directory.EnumerateFiles(buildToolsDirectory, "aapt.exe", SearchOption.AllDirectories)
                    .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(fromBuildTools))
                {
                    return fromBuildTools;
                }
            }
        }

        return null;
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort only; package name parsing has other fallbacks.
        }
    }

    private static string? TryReadPackageNameFromInstallScript(string scriptPath)
    {
        try
        {
            var text = File.ReadAllText(scriptPath);
            var explicitMatch = ExplicitPackageNameRegex.Match(text);
            if (explicitMatch.Success)
            {
                return explicitMatch.Groups["package"].Value;
            }

            return PackageNameFromInstallScriptRegex.Matches(text)
                .Select(match => match.Groups["package"].Value)
                .Where(value => !value.StartsWith("android.", StringComparison.OrdinalIgnoreCase))
                .Where(value => !value.StartsWith("com.android.", StringComparison.OrdinalIgnoreCase))
                .Where(value => !value.StartsWith("com.epicgames.", StringComparison.OrdinalIgnoreCase))
                .Where(value => value.Contains("com.", StringComparison.OrdinalIgnoreCase) ||
                                value.Contains("org.", StringComparison.OrdinalIgnoreCase) ||
                                value.Contains("net.", StringComparison.OrdinalIgnoreCase))
                .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .Select(group => group.Key)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static void PruneArchiveToAndroidRoot(string archiveDirectoryPath, string androidRoot)
    {
        var androidRootFullPath = Path.GetFullPath(androidRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        foreach (var entry in Directory.EnumerateFileSystemEntries(archiveDirectoryPath))
        {
            var entryFullPath = Path.GetFullPath(entry)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(entryFullPath, androidRootFullPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Directory.Exists(entry))
            {
                Directory.Delete(entry, recursive: true);
            }
            else
            {
                File.Delete(entry);
            }
        }
    }

    private static bool IsContainerFile(string path)
    {
        return ContainerExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsPreferredApkName(string path)
    {
        var fileName = Path.GetFileName(path);
        return !fileName.StartsWith("AFS_", StringComparison.OrdinalIgnoreCase) &&
               !fileName.Contains("unprotected", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnderAndroidOutput(string path, string archiveDirectoryPath)
    {
        var androidRoot = Path.Combine(archiveDirectoryPath, AndroidDirectoryName);
        var relativePath = Path.GetRelativePath(Path.GetFullPath(androidRoot), Path.GetFullPath(path));
        return !relativePath.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relativePath);
    }

    private static string ToManifestRelativePath(string androidRoot, string path)
    {
        return Path.GetRelativePath(androidRoot, path).Replace('\\', '/');
    }

    private static JsonSerializerOptions CreateManifestJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static string CreateInstallScript(AndroidPackageArtifactManifest manifest)
    {
        var files = string.Join(
            Environment.NewLine,
            manifest.Files.Select(file => $"  '{EscapePowerShellSingleQuoted(file.RelativePath)}'"));

        return $$"""
$ErrorActionPreference = "Stop"
$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$Device = ""
$CleanData = $false
$Launch = $false
$PackageName = '{{EscapePowerShellSingleQuoted(manifest.PackageName)}}'
$ProjectName = '{{EscapePowerShellSingleQuoted(manifest.ProjectName)}}'
$ApkRelativePath = '{{EscapePowerShellSingleQuoted(manifest.ApkPath)}}'
$DataRootRelativePath = '{{EscapePowerShellSingleQuoted(manifest.DataRoot)}}'
$Files = @(
{{files}}
)

for ($i = 0; $i -lt $args.Count; $i++) {
    switch ($args[$i]) {
        "--clean-data" { $CleanData = $true }
        "-CleanData" { $CleanData = $true }
        "--launch" { $Launch = $true }
        "-Launch" { $Launch = $true }
        "-s" {
            if ($i + 1 -ge $args.Count) { throw "-s requires a device serial." }
            $Device = $args[$i + 1]
            $i++
        }
        "--device" {
            if ($i + 1 -ge $args.Count) { throw "--device requires a device serial." }
            $Device = $args[$i + 1]
            $i++
        }
        "-Device" {
            if ($i + 1 -ge $args.Count) { throw "-Device requires a device serial." }
            $Device = $args[$i + 1]
            $i++
        }
        default { throw "Unknown argument: $($args[$i])" }
    }
}

function Find-Adb {
    $candidates = @()
    if ($env:ANDROID_HOME) { $candidates += Join-Path $env:ANDROID_HOME "platform-tools\adb.exe" }
    if ($env:ANDROID_SDK_ROOT) { $candidates += Join-Path $env:ANDROID_SDK_ROOT "platform-tools\adb.exe" }
    $fromPath = Get-Command adb -ErrorAction SilentlyContinue
    if ($fromPath) { $candidates += $fromPath.Source }

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) {
            return $candidate
        }
    }

    throw "adb.exe was not found. Set ANDROID_HOME/ANDROID_SDK_ROOT or add adb to PATH."
}

function Invoke-Adb {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$AdbArgs)

    $fullArgs = @()
    if ($Device) {
        $fullArgs += "-s"
        $fullArgs += $Device
    }
    $fullArgs += $AdbArgs

    & $Adb @fullArgs
    if ($LASTEXITCODE -ne 0) {
        throw "adb failed: $($fullArgs -join ' ')"
    }
}

function Get-RemoteFileSize {
    param([string]$RemotePath)

    $deviceArgs = @()
    if ($Device) {
        $deviceArgs += "-s"
        $deviceArgs += $Device
    }

    $output = & $Adb @deviceArgs shell "stat -c %s '$RemotePath' 2>/dev/null"
    if ($LASTEXITCODE -eq 0) {
        $text = ($output | Select-Object -First 1).ToString().Trim()
        $value = 0L
        if ([long]::TryParse($text, [ref]$value)) {
            return $value
        }
    }

    return $null
}

$Adb = Find-Adb
$ApkPath = Join-Path $ScriptRoot $ApkRelativePath
$DataRoot = Join-Path $ScriptRoot $DataRootRelativePath
$RemoteDir = "/sdcard/Android/data/$PackageName/files/UnrealGame/$ProjectName/$ProjectName/Content/Paks"

if (!(Test-Path $ApkPath)) { throw "APK not found: $ApkPath" }
if (!(Test-Path $DataRoot)) { throw "Data root not found: $DataRoot" }

Write-Host "Using adb: $Adb"
Write-Host "Installing APK: $ApkPath"
Invoke-Adb install -r -d $ApkPath

Write-Host "Preparing remote data directory: $RemoteDir"
if ($CleanData) {
    Write-Host "Cleaning existing remote Paks..."
    Invoke-Adb shell "rm -rf '$RemoteDir'"
}
Invoke-Adb shell "mkdir -p '$RemoteDir'"

$pushed = 0
$skipped = 0
$totalBytes = 0L
$stopwatchAll = [System.Diagnostics.Stopwatch]::StartNew()

foreach ($relativePath in $Files) {
    $localPath = Join-Path $ScriptRoot $relativePath
    if (!(Test-Path $localPath)) {
        throw "Data file listed in manifest is missing: $localPath"
    }

    $fileInfo = Get-Item $localPath
    $remotePath = "$RemoteDir/$($fileInfo.Name)"
    $remoteSize = Get-RemoteFileSize $remotePath
    $totalBytes += $fileInfo.Length

    if ($remoteSize -eq $fileInfo.Length) {
        $skipped++
        Write-Host ("SKIP {0} ({1:n0} bytes)" -f $fileInfo.Name, $fileInfo.Length)
        continue
    }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Invoke-Adb push $localPath $remotePath
    $sw.Stop()
    $pushed++
    Write-Host ("PUSH {0} ({1:n0} bytes, {2:n1}s)" -f $fileInfo.Name, $fileInfo.Length, $sw.Elapsed.TotalSeconds)
}

$stopwatchAll.Stop()
Write-Host ("Done. Pushed {0}, skipped {1}, total {2:n0} bytes, elapsed {3:n1}s." -f $pushed, $skipped, $totalBytes, $stopwatchAll.Elapsed.TotalSeconds)

if ($Launch) {
    Write-Host "Launching $PackageName"
    Invoke-Adb shell monkey -p $PackageName -c android.intent.category.LAUNCHER 1
}
""";
    }

    private static string EscapePowerShellSingleQuoted(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    public sealed class AndroidPackageArtifactManifest
    {
        public string ProjectName { get; set; } = string.Empty;

        public string PackageName { get; set; } = string.Empty;

        public Guid BuildId { get; set; }

        public string Revision { get; set; } = string.Empty;

        public string PackagingMode { get; set; } = string.Empty;

        public string ApkPath { get; set; } = string.Empty;

        public string DataRoot { get; set; } = string.Empty;

        public long ApkSizeBytes { get; set; }

        public long TotalDataSizeBytes { get; set; }

        public string GeneratedAtUtc { get; set; } = string.Empty;

        public List<AndroidPackageArtifactFileEntry> Files { get; set; } = new();
    }

    public sealed record AndroidPackageArtifactFileEntry(
        string RelativePath,
        string FileName,
        long SizeBytes,
        DateTime LastWriteTimeUtc);
}
