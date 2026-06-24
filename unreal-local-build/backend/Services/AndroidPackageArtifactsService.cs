using System.Globalization;
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

    private const string AndroidPackageNamePattern = @"[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z][A-Za-z0-9_]*)+";
    private static readonly string[] ContainerExtensions = [".pak", ".utoc", ".ucas", ".sig"];
    private static readonly string[] IgnoredStagedLooseFileNames = [];
    private static readonly JsonSerializerOptions ManifestJsonOptions = CreateManifestJsonOptions();
    private static readonly Regex AndroidPackageNameRegex = new($"^{AndroidPackageNamePattern}$", RegexOptions.Compiled);
    private static readonly Regex PackageNameFromObbRegex = new($@"^(?:main|patch|overflow\d+)\.\d+\.(?<package>{AndroidPackageNamePattern})\.obb$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ObbFileNameRegex = new($@"^(?<kind>main|patch|overflow(?<overflowIndex>\d+))\.(?<version>\d+)\.(?<package>{AndroidPackageNamePattern})\.obb$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ExplicitPackageNameRegex = new($@"(?:set\s+)?(?:package|packagename)\s*=\s*[""']?(?<package>{AndroidPackageNamePattern})[""']?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PackageNameFromInstallScriptRegex = new($@"(?<package>{AndroidPackageNamePattern})", RegexOptions.Compiled);
    private static readonly Regex AaptPackageNameRegex = new($@"package:\s+name='(?<package>{AndroidPackageNamePattern})'", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PakChunkFileNameRegex = new(@"^pakchunk(?<chunkId>\d+).*?\.(?:pak|utoc|ucas|sig)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex[] PackageNameFromTextRegexes =
    [
        new($@"Using package name:\s*'(?<package>{AndroidPackageNamePattern})'", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new($@"\bPACKAGE_NAME\s*=\s*(?<package>{AndroidPackageNamePattern})", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new($@"\bPackageName\s*=\s*(?<package>{AndroidPackageNamePattern})", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new($@"pkgName:(?<package>{AndroidPackageNamePattern})", RegexOptions.IgnoreCase | RegexOptions.Compiled)
    ];

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
        if (!IsSafeAndroidPathSegment(stagedProjectName))
        {
            throw new InvalidOperationException(
                $"Staged Android project name '{stagedProjectName}' is not safe for the ExternalFilesDir data path. " +
                "Use a project/target name containing only letters, numbers, underscore, dash, or dot.");
        }

        var packageName = ResolvePackageName(project, build, build.ArchiveDirectoryPath, apkPath, stagedProjectName);
        var stagedRoot = LocateStagedRootFromPakDirectory(pakDirectory)
            ?? throw new DirectoryNotFoundException($"No staged Android root directory was found for Paks directory {pakDirectory}.");
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
        var dataOutputDirectory = Path.Combine(androidRoot, "data", stagedProjectName);
        var obbOutputDirectory = Path.Combine(androidRoot, "obb");
        Directory.CreateDirectory(apkOutputDirectory);
        Directory.CreateDirectory(dataOutputDirectory);
        Directory.CreateDirectory(obbOutputDirectory);

        var apkOutputPath = Path.Combine(apkOutputDirectory, Path.GetFileName(apkPath));
        File.Copy(apkPath, apkOutputPath, overwrite: true);

        var manifestFiles = new List<AndroidPackageArtifactFileEntry>();
        var obbFiles = LocateObbSupportFiles(build.ArchiveDirectoryPath, packageName, androidRoot, obbOutputDirectory);
        foreach (var sourceFile in containerFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var stageRelativePath = ToManifestRelativePath(stagedRoot, sourceFile);
            CopyExternalDataFile(
                sourceFile,
                dataOutputDirectory,
                androidRoot,
                stageRelativePath,
                isContainer: true,
                manifestFiles);
        }

        foreach (var sourceFile in LocateStagedLooseFiles(stagedRoot, pakDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var stageRelativePath = ToManifestRelativePath(stagedRoot, sourceFile);
            CopyExternalDataFile(
                sourceFile,
                dataOutputDirectory,
                androidRoot,
                stageRelativePath,
                isContainer: false,
                manifestFiles);
        }

        var manifestChunks = CreateChunkEntries(manifestFiles);

        var manifest = new AndroidPackageArtifactManifest
        {
            ProjectName = stagedProjectName,
            PackageName = packageName,
            BuildId = build.Id,
            Revision = build.DisplayRevision,
            PackagingMode = build.AndroidPackagingMode.ToString(),
            ApkPath = ToManifestRelativePath(androidRoot, apkOutputPath),
            DataRoot = $"data/{stagedProjectName}",
            ObbRoot = obbFiles.Count == 0 ? string.Empty : "obb",
            ApkSizeBytes = new FileInfo(apkOutputPath).Length,
            TotalDataSizeBytes = manifestFiles.Sum(item => item.SizeBytes),
            GeneratedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            Files = manifestFiles,
            Chunks = manifestChunks,
            ObbFiles = obbFiles
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

        var installBatchPath = Path.Combine(androidRoot, CreateInstallBatchFileName(manifest));
        await File.WriteAllTextAsync(
            installBatchPath,
            CreateInstallBatchScript(manifest),
            new UTF8Encoding(false),
            cancellationToken);

        var uninstallBatchPath = Path.Combine(androidRoot, CreateUninstallBatchFileName(manifest));
        await File.WriteAllTextAsync(
            uninstallBatchPath,
            CreateUninstallBatchScript(manifest),
            new UTF8Encoding(false),
            cancellationToken);

        PruneArchiveToAndroidRoot(build.ArchiveDirectoryPath, androidRoot);

        build.AndroidPackageManifestPath = manifestPath;
        build.AndroidInstallScriptPath = installScriptPath;

        await log.WriteLineAsync($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] Android ExternalFilesIoStore artifact prepared.");
        await log.WriteLineAsync($"  APK: {manifest.ApkPath} ({manifest.ApkSizeBytes} bytes)");
        await log.WriteLineAsync($"  Data: {manifest.DataRoot} ({manifest.Files.Count} files, {manifest.TotalDataSizeBytes} bytes)");
        if (manifest.ObbFiles.Count > 0)
        {
            await log.WriteLineAsync($"  OBB support: {manifest.ObbFiles.Count} files");
        }
        await log.WriteLineAsync($"  Chunks: {manifest.Chunks.Count}");
        await log.WriteLineAsync($"  Package: {manifest.PackageName}");

        logger.LogInformation(
            "Prepared Android external data artifacts for build {BuildId}. Package={PackageName}, Project={ProjectName}, FileCount={FileCount}, ChunkCount={ChunkCount}, TotalDataSizeBytes={TotalDataSizeBytes}",
            build.Id,
            manifest.PackageName,
            manifest.ProjectName,
            manifest.Files.Count,
            manifest.Chunks.Count,
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

    private static string? LocateStagedRootFromPakDirectory(string pakDirectory)
    {
        var contentDirectory = Directory.GetParent(pakDirectory);
        var projectDirectory = contentDirectory?.Parent;
        return projectDirectory?.Parent?.FullName;
    }

    private static IEnumerable<string> LocateStagedLooseFiles(string stagedRoot, string pakDirectory)
    {
        var pakDirectoryFullPath = Path.GetFullPath(pakDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return Directory.EnumerateFiles(stagedRoot, "*", SearchOption.AllDirectories)
            .Where(path => !IsUnderDirectory(path, pakDirectoryFullPath))
            .Where(path => !IsIgnoredStagedLooseFile(path))
            .OrderBy(path => ToManifestRelativePath(stagedRoot, path), StringComparer.OrdinalIgnoreCase);
    }

    private static void CopyExternalDataFile(
        string sourceFile,
        string dataOutputDirectory,
        string androidRoot,
        string stageRelativePath,
        bool isContainer,
        List<AndroidPackageArtifactFileEntry> manifestFiles)
    {
        var destinationFile = Path.Combine(
            dataOutputDirectory,
            stageRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
        File.Copy(sourceFile, destinationFile, overwrite: true);

        var info = new FileInfo(destinationFile);
        var chunk = isContainer ? TryInferChunk(info.Name) : null;
        manifestFiles.Add(new AndroidPackageArtifactFileEntry(
            ToManifestRelativePath(androidRoot, destinationFile),
            stageRelativePath,
            info.Name,
            info.Length,
            info.LastWriteTimeUtc,
            isContainer,
            chunk?.ChunkId,
            chunk?.ChunkName));
    }

    private static List<AndroidPackageArtifactObbEntry> LocateObbSupportFiles(
        string archiveDirectoryPath,
        string packageName,
        string androidRoot,
        string obbOutputDirectory)
    {
        var entries = new List<AndroidPackageArtifactObbEntry>();
        foreach (var sourceFile in Directory.EnumerateFiles(archiveDirectoryPath, "*.obb", SearchOption.AllDirectories)
                     .Where(path => !IsUnderAndroidOutput(path, archiveDirectoryPath))
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(sourceFile);
            var match = ObbFileNameRegex.Match(fileName);
            if (!match.Success)
            {
                continue;
            }

            var filePackageName = match.Groups["package"].Value;
            if (!string.Equals(filePackageName, packageName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destinationPath = Path.Combine(obbOutputDirectory, fileName);
            File.Copy(sourceFile, destinationPath, overwrite: true);

            var info = new FileInfo(destinationPath);
            entries.Add(new AndroidPackageArtifactObbEntry(
                ToManifestRelativePath(androidRoot, destinationPath),
                info.Name,
                info.Length,
                info.LastWriteTimeUtc,
                match.Groups["kind"].Value,
                ParseNullableInt(match.Groups["overflowIndex"].Value),
                match.Groups["version"].Value));
        }

        return entries;
    }

    private static string ResolveStagedProjectName(string pakDirectory, ProjectConfig project)
    {
        var contentDirectory = Directory.GetParent(pakDirectory);
        var stagedProjectDirectory = contentDirectory?.Parent;
        var stagedProjectName = stagedProjectDirectory?.Name;
        return string.IsNullOrWhiteSpace(stagedProjectName) ? project.Name : stagedProjectName;
    }

    private static string ResolvePackageName(
        ProjectConfig project,
        BuildRecord build,
        string archiveDirectoryPath,
        string apkPath,
        string projectName)
    {
        var fromApk = TryReadPackageNameFromApk(apkPath);
        if (IsValidPackageName(fromApk))
        {
            return fromApk!;
        }

        var fromArchivePackageInfo = TryReadPackageNameFromArchivePackageInfo(apkPath);
        if (IsValidPackageName(fromArchivePackageInfo))
        {
            return fromArchivePackageInfo!;
        }

        var fromBuildLog = TryReadPackageNameFromBuildLog(build.LogFilePath);
        if (IsValidPackageName(fromBuildLog))
        {
            return fromBuildLog!;
        }

        var fromInstallScript = Directory.EnumerateFiles(archiveDirectoryPath, "Install_*.bat", SearchOption.AllDirectories)
            .Select(TryReadPackageNameFromInstallScript)
            .FirstOrDefault(IsValidPackageName);
        if (IsValidPackageName(fromInstallScript))
        {
            return fromInstallScript!;
        }

        var fromObb = Directory.EnumerateFiles(archiveDirectoryPath, "*.obb", SearchOption.AllDirectories)
            .Select(path => PackageNameFromObbRegex.Match(Path.GetFileName(path)))
            .Where(match => match.Success)
            .Select(match => match.Groups["package"].Value)
            .FirstOrDefault(IsValidPackageName);
        if (IsValidPackageName(fromObb))
        {
            return fromObb!;
        }

        var fromProjectPackageInfo = TryReadPackageNameFromProjectPackageInfo(project);
        if (IsValidPackageName(fromProjectPackageInfo))
        {
            return fromProjectPackageInfo!;
        }

        throw new InvalidOperationException(
            "Unable to resolve the Android package name from the APK, packageInfo.txt, build log, install script, or OBB names. " +
            $"Build {build.Id} cannot prepare external data artifacts for project {projectName} because pushing data to a guessed package directory is unsafe.");
    }

    private static string? TryReadPackageNameFromApk(string apkPath)
    {
        try
        {
            foreach (var aaptPath in LocateAaptTools())
            {
                var packageName = TryReadPackageNameFromApkWithTool(apkPath, aaptPath);
                if (IsValidPackageName(packageName))
                {
                    return packageName;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadPackageNameFromApkWithTool(string apkPath, string aaptPath)
    {
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

        var match = AaptPackageNameRegex.Match(stdout);
        return match.Success ? match.Groups["package"].Value : null;
    }

    private static string? TryReadPackageNameFromArchivePackageInfo(string apkPath)
    {
        var apkDirectory = Path.GetDirectoryName(apkPath);
        return string.IsNullOrWhiteSpace(apkDirectory)
            ? null
            : TryReadFirstPackageInfoLine(Path.Combine(apkDirectory, "packageInfo.txt"));
    }

    private static string? TryReadPackageNameFromProjectPackageInfo(ProjectConfig project)
    {
        var projectDirectory = Path.GetDirectoryName(project.UProjectPath);
        return string.IsNullOrWhiteSpace(projectDirectory)
            ? null
            : TryReadFirstPackageInfoLine(Path.Combine(projectDirectory, "Binaries", "Android", "packageInfo.txt"));
    }

    private static string? TryReadFirstPackageInfoLine(string packageInfoPath)
    {
        try
        {
            if (!File.Exists(packageInfoPath))
            {
                return null;
            }

            var line = File.ReadLines(packageInfoPath).FirstOrDefault()?.Trim();
            return IsValidPackageName(line) ? line : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadPackageNameFromBuildLog(string logPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
            {
                return null;
            }

            foreach (var line in File.ReadLines(logPath))
            {
                foreach (var regex in PackageNameFromTextRegexes)
                {
                    var match = regex.Match(line);
                    if (match.Success)
                    {
                        var packageName = match.Groups["package"].Value;
                        if (IsValidPackageName(packageName))
                        {
                            return packageName;
                        }
                    }
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static IEnumerable<string> LocateAaptTools()
    {
        var sdkRoot = AndroidToolchain.ResolveSdkRoot();
        if (!string.IsNullOrWhiteSpace(sdkRoot))
        {
            var buildToolsDirectory = Path.Combine(sdkRoot, "build-tools");
            if (Directory.Exists(buildToolsDirectory))
            {
                foreach (var fromBuildTools in Directory.EnumerateFiles(buildToolsDirectory, "aapt.exe", SearchOption.AllDirectories)
                    .Concat(Directory.EnumerateFiles(buildToolsDirectory, "aapt2.exe", SearchOption.AllDirectories))
                    .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                    .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    yield return fromBuildTools;
                }
            }
        }
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

    private static bool IsIgnoredStagedLooseFile(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.StartsWith("Manifest_", StringComparison.OrdinalIgnoreCase) ||
               IgnoredStagedLooseFileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsUnderDirectory(string path, string directoryPath)
    {
        var relativePath = Path.GetRelativePath(
            directoryPath,
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return !relativePath.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relativePath);
    }

    private static AndroidPackageArtifactChunkIdentity? TryInferChunk(string fileName)
    {
        var match = PakChunkFileNameRegex.Match(fileName);
        if (!match.Success ||
            !int.TryParse(match.Groups["chunkId"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var chunkId))
        {
            return null;
        }

        return new AndroidPackageArtifactChunkIdentity(chunkId, $"chunk{chunkId}");
    }

    private static List<AndroidPackageArtifactChunkEntry> CreateChunkEntries(
        IReadOnlyCollection<AndroidPackageArtifactFileEntry> files)
    {
        return files
            .Where(file => file.ChunkId.HasValue)
            .GroupBy(file => file.ChunkId!.Value)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var chunkFiles = group
                    .OrderBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return new AndroidPackageArtifactChunkEntry(
                    group.Key,
                    chunkFiles.FirstOrDefault(file => !string.IsNullOrWhiteSpace(file.ChunkName))?.ChunkName ?? $"chunk{group.Key}",
                    chunkFiles.Count,
                    chunkFiles.Sum(file => file.SizeBytes),
                    chunkFiles.Select(file => file.RelativePath).ToList());
            })
            .ToList();
    }

    private static bool IsValidPackageName(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && AndroidPackageNameRegex.IsMatch(value.Trim());
    }

    private static int? ParseNullableInt(string? value)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static bool IsSafeAndroidPathSegment(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.');
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
            manifest.Files.Select(file =>
                $"  [pscustomobject]@{{ Path = '{EscapePowerShellSingleQuoted(file.RelativePath)}'; StagePath = '{EscapePowerShellSingleQuoted(file.StageRelativePath)}'; IsContainer = {FormatPowerShellBool(file.IsContainer)}; ChunkId = {FormatPowerShellNullableInt(file.ChunkId)}; ChunkName = '{EscapePowerShellSingleQuoted(file.ChunkName ?? string.Empty)}' }}"));
        var obbFiles = string.Join(
            Environment.NewLine,
            manifest.ObbFiles.Select(file =>
                $"  [pscustomobject]@{{ Path = '{EscapePowerShellSingleQuoted(file.RelativePath)}'; FileName = '{EscapePowerShellSingleQuoted(file.FileName)}'; Kind = '{EscapePowerShellSingleQuoted(file.Kind)}'; OverflowIndex = {FormatPowerShellNullableInt(file.OverflowIndex)}; Version = '{EscapePowerShellSingleQuoted(file.Version)}' }}"));

        return $$"""
$ErrorActionPreference = "Stop"
$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$Device = ""
$CleanData = $false
$Launch = $false
$PruneStale = $true
$ChunkFilter = $null
$PackageName = '{{EscapePowerShellSingleQuoted(manifest.PackageName)}}'
$ProjectName = '{{EscapePowerShellSingleQuoted(manifest.ProjectName)}}'
$ApkRelativePath = '{{EscapePowerShellSingleQuoted(manifest.ApkPath)}}'
$DataRootRelativePath = '{{EscapePowerShellSingleQuoted(manifest.DataRoot)}}'
$ObbRootRelativePath = '{{EscapePowerShellSingleQuoted(manifest.ObbRoot)}}'
$Files = @(
{{files}}
)
$ObbFiles = @(
{{obbFiles}}
)
$SelectedChunkIds = $null

for ($i = 0; $i -lt $args.Count; $i++) {
    switch ($args[$i]) {
        "--clean-data" { $CleanData = $true }
        "-CleanData" { $CleanData = $true }
        "--launch" { $Launch = $true }
        "-Launch" { $Launch = $true }
        "--no-prune-stale" { $PruneStale = $false }
        "-NoPruneStale" { $PruneStale = $false }
        "--chunks" {
            if ($i + 1 -ge $args.Count) { throw "--chunks requires a comma-separated chunk id list." }
            $ChunkFilter = $args[$i + 1]
            $i++
        }
        "-Chunks" {
            if ($i + 1 -ge $args.Count) { throw "-Chunks requires a comma-separated chunk id list." }
            $ChunkFilter = $args[$i + 1]
            $i++
        }
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

function ConvertTo-ChunkIdSet {
    param([string]$Value)

    $set = @{}
    foreach ($part in ($Value -split ',')) {
        $text = $part.Trim()
        if (!$text) {
            continue
        }

        $chunkId = 0
        if (![int]::TryParse($text, [ref]$chunkId)) {
            throw "Invalid chunk id '$text'. Use a comma-separated numeric list, for example: --chunks 0,101"
        }

        $set[$chunkId] = $true
    }

    if ($set.Count -eq 0) {
        throw "Chunk filter is empty. Use a comma-separated numeric list, for example: --chunks 0,101"
    }

    return $set
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

function ConvertTo-AdbShellSingleQuoted {
    param([string]$Value)

    return "'" + ($Value -replace "'", "'\''") + "'"
}

function Test-InstalledPackage {
    param([string]$Name)

    $quotedPackage = ConvertTo-AdbShellSingleQuoted $Name
    $deviceArgs = @()
    if ($Device) {
        $deviceArgs += "-s"
        $deviceArgs += $Device
    }

    $output = & $Adb @deviceArgs shell "pm path $quotedPackage 2>/dev/null"
    if ($LASTEXITCODE -ne 0) {
        return $false
    }

    return (($output | Out-String).Trim().Length -gt 0)
}

function Get-RemoteFileSize {
    param([string]$RemotePath)

    $deviceArgs = @()
    if ($Device) {
        $deviceArgs += "-s"
        $deviceArgs += $Device
    }

    $quotedPath = ConvertTo-AdbShellSingleQuoted $RemotePath
    $commands = @(
        "stat -c %s $quotedPath 2>/dev/null",
        "toybox stat -c %s $quotedPath 2>/dev/null",
        "wc -c < $quotedPath 2>/dev/null"
    )

    foreach ($command in $commands) {
        $output = & $Adb @deviceArgs shell $command
        if ($LASTEXITCODE -eq 0) {
            $text = ($output | Select-Object -First 1).ToString().Trim()
            $value = 0L
            if ([long]::TryParse($text, [ref]$value)) {
                return $value
            }
        }
    }

    $output = & $Adb @deviceArgs shell "ls -ln $quotedPath 2>/dev/null"
    if ($LASTEXITCODE -eq 0) {
        $line = ($output | Select-Object -First 1).ToString().Trim()
        $parts = $line -split '\s+'
        $value = 0L
        if ($parts.Count -ge 5 -and [long]::TryParse($parts[4], [ref]$value)) {
            return $value
        }
    }

    return $null
}

function Get-RemoteContainerFiles {
    param([string]$RemoteDirectory)

    $deviceArgs = @()
    if ($Device) {
        $deviceArgs += "-s"
        $deviceArgs += $Device
    }

    $quotedDirectory = ConvertTo-AdbShellSingleQuoted $RemoteDirectory
    $output = & $Adb @deviceArgs shell "ls -1 $quotedDirectory 2>/dev/null"
    if ($LASTEXITCODE -ne 0) {
        return @()
    }

    return @($output |
        ForEach-Object { $_.ToString().Trim() } |
        Where-Object { $_ -match '\.(pak|utoc|ucas|sig)$' })
}

function Get-ExternalStorageRoot {
    $deviceArgs = @()
    if ($Device) {
        $deviceArgs += "-s"
        $deviceArgs += $Device
    }

    $commands = @(
        "echo `$EXTERNAL_STORAGE",
        "echo /sdcard"
    )

    foreach ($command in $commands) {
        $output = & $Adb @deviceArgs shell $command
        if ($LASTEXITCODE -ne 0) {
            continue
        }

        $value = ($output | Select-Object -First 1).ToString().Trim()
        if (![string]::IsNullOrWhiteSpace($value)) {
            return $value
        }
    }

    return "/sdcard"
}

function Get-RemotePathForFile {
    param([object]$File)

    return "$RemoteRoot/$($File.StagePath)"
}

function Remove-RemoteContainerFile {
    param([string]$RemotePath)

    $quotedPath = ConvertTo-AdbShellSingleQuoted $RemotePath
    Invoke-Adb shell "rm -f $quotedPath"
}

function Get-ContainerChunkIdFromFileName {
    param([string]$FileName)

    $match = [regex]::Match($FileName, '^pakchunk(?<chunkId>\d+).*?\.(pak|utoc|ucas|sig)$', 'IgnoreCase')
    if (!$match.Success) {
        return $null
    }

    $chunkId = 0
    if ([int]::TryParse($match.Groups['chunkId'].Value, [ref]$chunkId)) {
        return $chunkId
    }

    return $null
}

$Adb = Find-Adb
$ApkPath = Join-Path $ScriptRoot $ApkRelativePath
$DataRoot = Join-Path $ScriptRoot $DataRootRelativePath
$ObbRootLocal = if ([string]::IsNullOrWhiteSpace($ObbRootRelativePath)) { $null } else { Join-Path $ScriptRoot $ObbRootRelativePath }
$ExternalStorageRoot = Get-ExternalStorageRoot
$RemoteTempRoot = "$ExternalStorageRoot/Download/obb-install-temp/$PackageName"
$RemoteRoot = "/sdcard/Android/data/$PackageName/files/UnrealGame/$ProjectName"
$RemoteContainerDir = "$RemoteRoot/$ProjectName/Content/Paks"
$RemoteObbRoot = "/sdcard/Android/obb/$PackageName"
$SelectedFiles = @($Files)
if ($ChunkFilter) {
    $SelectedChunkIds = ConvertTo-ChunkIdSet $ChunkFilter
    if ($CleanData) {
        throw "--clean-data cannot be combined with --chunks because it deletes all remote chunks. Run without --chunks for a full refresh, or omit --clean-data for selective chunk sync."
    }

    $SelectedContainers = @($Files | Where-Object { $_.IsContainer -and $null -ne $_.ChunkId -and $SelectedChunkIds.ContainsKey([int]$_.ChunkId) })
    if ($SelectedContainers.Count -eq 0) {
        throw "No container files matched --chunks $ChunkFilter. Check android-package-manifest.json for available chunks."
    }

    $SelectedFiles = @($Files | Where-Object { !$_.IsContainer }) + $SelectedContainers
}

if (!(Test-Path $ApkPath)) { throw "APK not found: $ApkPath" }
if (!(Test-Path $DataRoot)) { throw "Data root not found: $DataRoot" }
if ($ObbFiles.Count -gt 0 -and (!$ObbRootLocal -or !(Test-Path $ObbRootLocal))) { throw "OBB root not found: $ObbRootLocal" }

Write-Host "Using adb: $Adb"
if ($ChunkFilter) {
    Write-Host ("Chunk filter: {0} ({1} files). Stale remote cleanup is scoped to selected chunks." -f $ChunkFilter, $SelectedFiles.Count)
}
Write-Host "Installing APK: $ApkPath"
Invoke-Adb install -r -d $ApkPath
if (!(Test-InstalledPackage $PackageName)) {
    throw "Installed APK did not register package $PackageName. Refusing to push external data to a guessed directory."
}

Write-Host "Preparing remote data root: $RemoteRoot"
$QuotedRemoteRoot = ConvertTo-AdbShellSingleQuoted $RemoteRoot
if ($CleanData) {
    Write-Host "Cleaning existing remote external data..."
    Invoke-Adb shell "rm -rf $QuotedRemoteRoot"
    if ($ObbFiles.Count -gt 0) {
        $QuotedRemoteObbRoot = ConvertTo-AdbShellSingleQuoted $RemoteObbRoot
        Write-Host "Cleaning existing remote OBB support files..."
        Invoke-Adb shell "rm -rf $QuotedRemoteObbRoot"
    }
}
Invoke-Adb shell "mkdir -p $QuotedRemoteRoot"
if ($ObbFiles.Count -gt 0) {
    Invoke-Adb shell ("mkdir -p " + (ConvertTo-AdbShellSingleQuoted $RemoteObbRoot))
    Invoke-Adb shell ("mkdir -p " + (ConvertTo-AdbShellSingleQuoted $RemoteTempRoot))
}

$pushed = 0
$skipped = 0
$totalBytes = 0L
$stopwatchAll = [System.Diagnostics.Stopwatch]::StartNew()
$expectedRemoteNames = @{}
foreach ($file in $SelectedFiles) {
    if ($file.IsContainer) {
        $expectedRemoteNames[[System.IO.Path]::GetFileName($file.Path)] = $true
    }
}

$removed = 0
if ($PruneStale) {
    foreach ($remoteName in Get-RemoteContainerFiles $RemoteContainerDir) {
        if ($expectedRemoteNames.ContainsKey($remoteName)) {
            continue
        }

        if ($SelectedChunkIds) {
            $remoteChunkId = Get-ContainerChunkIdFromFileName $remoteName
            if ($null -eq $remoteChunkId -or !$SelectedChunkIds.ContainsKey([int]$remoteChunkId)) {
                continue
            }
        }

        Remove-RemoteContainerFile "$RemoteContainerDir/$remoteName"
        $removed++
        Write-Host "REMOVE stale $remoteName"
    }
} else {
    Write-Host "Skipping stale remote container cleanup."
}

foreach ($file in $SelectedFiles) {
    $localPath = Join-Path $ScriptRoot $file.Path
    if (!(Test-Path $localPath)) {
        throw "Data file listed in manifest is missing: $localPath"
    }

    $fileInfo = Get-Item $localPath
    $remotePath = Get-RemotePathForFile $file
    $remoteParent = $remotePath.Substring(0, $remotePath.LastIndexOf('/'))
    Invoke-Adb shell ("mkdir -p " + (ConvertTo-AdbShellSingleQuoted $remoteParent))
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
    $verifiedRemoteSize = Get-RemoteFileSize $remotePath
    if ($null -eq $verifiedRemoteSize) {
        Write-Warning "Could not verify remote size for $($fileInfo.Name)."
    } elseif ($verifiedRemoteSize -ne $fileInfo.Length) {
        throw "Remote file size mismatch after push: $($fileInfo.Name). Local=$($fileInfo.Length), Remote=$verifiedRemoteSize"
    }

    $pushed++
    Write-Host ("PUSH {0} ({1:n0} bytes, {2:n1}s)" -f $fileInfo.Name, $fileInfo.Length, $sw.Elapsed.TotalSeconds)
}

$stopwatchAll.Stop()
if ($ObbFiles.Count -gt 0) {
    foreach ($obb in $ObbFiles) {
        $localObbPath = Join-Path $ScriptRoot $obb.Path
        if (!(Test-Path $localObbPath)) {
            throw "OBB support file listed in manifest is missing: $localObbPath"
        }

        $remoteObbPath = "$RemoteObbRoot/$($obb.FileName)"
        $tempObbPath = "$RemoteTempRoot/$($obb.FileName)"
        $localObbInfo = Get-Item $localObbPath
        $remoteObbSize = Get-RemoteFileSize $remoteObbPath
        if ($remoteObbSize -eq $localObbInfo.Length) {
            Write-Host ("SKIP OBB {0} ({1:n0} bytes)" -f $localObbInfo.Name, $localObbInfo.Length)
            continue
        }

        Invoke-Adb push $localObbPath $tempObbPath
        Invoke-Adb shell ("mkdir -p " + (ConvertTo-AdbShellSingleQuoted $RemoteObbRoot))
        Invoke-Adb shell ("mv " + (ConvertTo-AdbShellSingleQuoted $tempObbPath) + " " + (ConvertTo-AdbShellSingleQuoted $remoteObbPath))
        $verifiedObbSize = Get-RemoteFileSize $remoteObbPath
        if ($null -eq $verifiedObbSize) {
            Write-Warning "Could not verify remote OBB size for $($localObbInfo.Name)."
        } elseif ($verifiedObbSize -ne $localObbInfo.Length) {
            throw "Remote OBB file size mismatch after push: $($localObbInfo.Name). Local=$($localObbInfo.Length), Remote=$verifiedObbSize"
        }

        Write-Host ("PUSH OBB {0} ({1:n0} bytes)" -f $localObbInfo.Name, $localObbInfo.Length)
    }

    Invoke-Adb shell ("rm -rf " + (ConvertTo-AdbShellSingleQuoted $RemoteTempRoot))
}

$stopwatchAll.Stop()
Write-Host ("Done. Pushed {0}, skipped {1}, removed {2}, total {3:n0} bytes, elapsed {4:n1}s." -f $pushed, $skipped, $removed, $totalBytes, $stopwatchAll.Elapsed.TotalSeconds)

if ($Launch) {
    Write-Host "Launching $PackageName"
    Invoke-Adb shell monkey -p $PackageName -c android.intent.category.LAUNCHER 1
}
""";
    }

    private static string CreateInstallBatchFileName(AndroidPackageArtifactManifest manifest)
    {
        return $"Install_{CreateBatchScriptBaseName(manifest)}.bat";
    }

    private static string CreateUninstallBatchFileName(AndroidPackageArtifactManifest manifest)
    {
        return $"Uninstall_{CreateBatchScriptBaseName(manifest)}.bat";
    }

    private static string CreateBatchScriptBaseName(AndroidPackageArtifactManifest manifest)
    {
        var apkPath = manifest.ApkPath.Replace('/', Path.DirectorySeparatorChar);
        var apkBaseName = Path.GetFileNameWithoutExtension(apkPath);
        if (string.IsNullOrWhiteSpace(apkBaseName))
        {
            apkBaseName = manifest.ProjectName;
        }

        var invalidFileNameCharacters = Path.GetInvalidFileNameChars();
        var safeName = new string(apkBaseName
            .Select(ch => invalidFileNameCharacters.Contains(ch) ? '_' : ch)
            .ToArray());
        return string.IsNullOrWhiteSpace(safeName) ? "Android" : safeName;
    }

    private static string CreateInstallBatchScript(AndroidPackageArtifactManifest manifest)
    {
        var apkPath = manifest.ApkPath.Replace('/', '\\');
        return $$"""
@echo off
setlocal EnableExtensions
chcp 65001 >nul

set "ROOT=%~dp0"
set "APK_PATH=%ROOT%{{EscapeBatchValue(apkPath)}}"
set "INSTALL_PS1=%ROOT%{{InstallerFileName}}"

if not exist "%APK_PATH%" (
    echo APK not found:
    echo   "%APK_PATH%"
    echo.
    echo Files under "%ROOT%apk":
    dir /b "%ROOT%apk\*.apk" 2>nul
    echo.
    pause
    exit /b 1
)

if not exist "%INSTALL_PS1%" (
    echo External data installer script not found:
    echo   "%INSTALL_PS1%"
    echo.
    pause
    exit /b 1
)

call :FindAdb
if errorlevel 1 (
    echo.
    pause
    exit /b 1
)

echo Using adb: "%ADB%"
echo APK: "%APK_PATH%"
echo.
"%ADB%" devices
echo.
echo Installing APK and syncing external data to the connected device...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%INSTALL_PS1%"
if errorlevel 1 (
    echo.
    echo Install failed.
    pause
    exit /b 1
)

echo.
echo Install completed.
pause
exit /b 0

:FindAdb
set "ADB="
if defined ANDROID_HOME if exist "%ANDROID_HOME%\platform-tools\adb.exe" set "ADB=%ANDROID_HOME%\platform-tools\adb.exe"
if not defined ADB if defined ANDROID_SDK_ROOT if exist "%ANDROID_SDK_ROOT%\platform-tools\adb.exe" set "ADB=%ANDROID_SDK_ROOT%\platform-tools\adb.exe"
if not defined ADB for %%I in (adb.exe) do set "ADB=%%~$PATH:I"
if not defined ADB (
    echo adb.exe was not found. Set ANDROID_HOME/ANDROID_SDK_ROOT or add adb to PATH.
    exit /b 1
)
exit /b 0
""";
    }

    private static string CreateUninstallBatchScript(AndroidPackageArtifactManifest manifest)
    {
        return $$"""
@echo off
setlocal EnableExtensions
chcp 65001 >nul

set "PACKAGE_NAME={{EscapeBatchValue(manifest.PackageName)}}"

call :FindAdb
if errorlevel 1 (
    echo.
    pause
    exit /b 1
)

echo Using adb: "%ADB%"
echo Package: %PACKAGE_NAME%
echo.
"%ADB%" devices
echo.
echo Uninstalling %PACKAGE_NAME% from the connected device...
"%ADB%" uninstall %PACKAGE_NAME%
if errorlevel 1 (
    echo.
    echo Uninstall failed.
    pause
    exit /b 1
)

echo.
echo Uninstall completed.
pause
exit /b 0

:FindAdb
set "ADB="
if defined ANDROID_HOME if exist "%ANDROID_HOME%\platform-tools\adb.exe" set "ADB=%ANDROID_HOME%\platform-tools\adb.exe"
if not defined ADB if defined ANDROID_SDK_ROOT if exist "%ANDROID_SDK_ROOT%\platform-tools\adb.exe" set "ADB=%ANDROID_SDK_ROOT%\platform-tools\adb.exe"
if not defined ADB for %%I in (adb.exe) do set "ADB=%%~$PATH:I"
if not defined ADB (
    echo adb.exe was not found. Set ANDROID_HOME/ANDROID_SDK_ROOT or add adb to PATH.
    exit /b 1
)
exit /b 0
""";
    }

    private static string FormatPowerShellNullableInt(int? value)
    {
        return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "$null";
    }

    private static string FormatPowerShellBool(bool value)
    {
        return value ? "$true" : "$false";
    }

    private static string EscapePowerShellSingleQuoted(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static string EscapeBatchValue(string value)
    {
        return value
            .Replace("%", "%%", StringComparison.Ordinal)
            .Replace("\"", string.Empty, StringComparison.Ordinal);
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

        public string ObbRoot { get; set; } = string.Empty;

        public long ApkSizeBytes { get; set; }

        public long TotalDataSizeBytes { get; set; }

        public string GeneratedAtUtc { get; set; } = string.Empty;

        public List<AndroidPackageArtifactFileEntry> Files { get; set; } = new();

        public List<AndroidPackageArtifactChunkEntry> Chunks { get; set; } = new();

        public List<AndroidPackageArtifactObbEntry> ObbFiles { get; set; } = new();
    }

    public sealed record AndroidPackageArtifactFileEntry(
        string RelativePath,
        string StageRelativePath,
        string FileName,
        long SizeBytes,
        DateTime LastWriteTimeUtc,
        bool IsContainer,
        int? ChunkId,
        string? ChunkName);

    public sealed record AndroidPackageArtifactChunkEntry(
        int ChunkId,
        string ChunkName,
        int FileCount,
        long TotalSizeBytes,
        List<string> Files);

    public sealed record AndroidPackageArtifactObbEntry(
        string RelativePath,
        string FileName,
        long SizeBytes,
        DateTime LastWriteTimeUtc,
        string Kind,
        int? OverflowIndex,
        string Version);

    private sealed record AndroidPackageArtifactChunkIdentity(int ChunkId, string ChunkName);
}
