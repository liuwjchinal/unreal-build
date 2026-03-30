using System.Diagnostics;
using Backend.Contracts;
using Backend.Data;
using Backend.Models;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

public sealed class ProjectValidator(IDbContextFactory<BuildDbContext> dbFactory, ILogger<ProjectValidator> logger)
{
    private static readonly string[] AndroidSupportedFlavors = ["ASTC"];

    public Task<Dictionary<string, string[]>> ValidateProjectAsync(
        UpsertProjectRequest request,
        Guid? existingProjectId,
        CancellationToken cancellationToken)
    {
        return ValidateProjectCoreAsync(
            request,
            existingProjectId,
            includeSvnStatus: false,
            platform: null,
            targetType: null,
            buildConfiguration: null,
            cancellationToken);
    }

    public Task<Dictionary<string, string[]>> ValidateBuildRequestAsync(
        ProjectConfig project,
        QueueBuildRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedRequest = new UpsertProjectRequest(
            project.ProjectKey,
            project.Name,
            project.WorkingCopyPath,
            project.UProjectPath,
            project.EngineRootPath,
            project.ArchiveRootPath,
            project.GameTarget,
            project.ClientTarget,
            project.ServerTarget,
            project.AndroidEnabled,
            project.AndroidTextureFlavor,
            project.AllowedBuildConfigurations,
            project.DefaultExtraUatArgs);

        return ValidateProjectCoreAsync(
            normalizedRequest,
            project.Id,
            includeSvnStatus: true,
            platform: request.Platform,
            targetType: request.TargetType,
            buildConfiguration: request.BuildConfiguration,
            cancellationToken);
    }

    private async Task<Dictionary<string, string[]>> ValidateProjectCoreAsync(
        UpsertProjectRequest request,
        Guid? existingProjectId,
        bool includeSvnStatus,
        BuildPlatform? platform,
        BuildTargetType? targetType,
        string? buildConfiguration,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        AddIf(string.IsNullOrWhiteSpace(request.Name), nameof(request.Name), "项目名称不能为空。");
        AddIf(string.IsNullOrWhiteSpace(request.WorkingCopyPath), nameof(request.WorkingCopyPath), "SVN 工作副本路径不能为空。");
        AddIf(string.IsNullOrWhiteSpace(request.UProjectPath), nameof(request.UProjectPath), ".uproject 路径不能为空。");
        AddIf(string.IsNullOrWhiteSpace(request.EngineRootPath), nameof(request.EngineRootPath), "Engine 根目录不能为空。");
        AddIf(string.IsNullOrWhiteSpace(request.ArchiveRootPath), nameof(request.ArchiveRootPath), "归档根目录不能为空。");

        if (!string.IsNullOrWhiteSpace(request.WorkingCopyPath) && !Directory.Exists(request.WorkingCopyPath))
        {
            Add(nameof(request.WorkingCopyPath), "SVN 工作副本路径不存在。");
        }

        if (!string.IsNullOrWhiteSpace(request.UProjectPath) && !File.Exists(request.UProjectPath))
        {
            Add(nameof(request.UProjectPath), ".uproject 文件不存在。");
        }

        var projectDirectory = string.IsNullOrWhiteSpace(request.UProjectPath)
            ? null
            : Path.GetDirectoryName(request.UProjectPath);

        if (!string.IsNullOrWhiteSpace(request.EngineRootPath))
        {
            if (!Directory.Exists(request.EngineRootPath))
            {
                Add(nameof(request.EngineRootPath), "Engine 根目录不存在。");
            }
            else
            {
                var runUatPath = Path.Combine(request.EngineRootPath, "Engine", "Build", "BatchFiles", "RunUAT.bat");
                if (!File.Exists(runUatPath))
                {
                    Add(nameof(request.EngineRootPath), @"未找到 Engine\Build\BatchFiles\RunUAT.bat。");
                }
            }
        }

        var normalizedAndroidTextureFlavor = string.IsNullOrWhiteSpace(request.AndroidTextureFlavor)
            ? "ASTC"
            : request.AndroidTextureFlavor.Trim();

        if (!AndroidSupportedFlavors.Contains(normalizedAndroidTextureFlavor, StringComparer.OrdinalIgnoreCase))
        {
            Add(nameof(request.AndroidTextureFlavor), "Android 第一版只支持 ASTC。");
        }

        var allowedConfigs = request.AllowedBuildConfigurations?
            .Select(item => item.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();

        if (allowedConfigs.Count == 0)
        {
            Add(nameof(request.AllowedBuildConfigurations), "至少需要配置一个可用构建配置，例如 Development 或 Shipping。");
        }

        if (!string.IsNullOrWhiteSpace(buildConfiguration) &&
            !allowedConfigs.Contains(buildConfiguration.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            Add(nameof(buildConfiguration), "该项目不允许当前构建配置。");
        }

        ValidateTargetField(nameof(request.GameTarget), request.GameTarget);
        ValidateTargetField(nameof(request.ClientTarget), request.ClientTarget);
        ValidateTargetField(nameof(request.ServerTarget), request.ServerTarget);

        var targetSearchRoots = GetTargetSearchRoots(projectDirectory).ToList();
        if (targetSearchRoots.Count > 0)
        {
            ValidateTargetFile(nameof(request.GameTarget), request.GameTarget, targetSearchRoots);
            ValidateTargetFile(nameof(request.ClientTarget), request.ClientTarget, targetSearchRoots);
            ValidateTargetFile(nameof(request.ServerTarget), request.ServerTarget, targetSearchRoots);
        }

        if (platform.HasValue && targetType.HasValue)
        {
            if (!BuildCommandFactory.SupportsTargetType(platform.Value, targetType.Value))
            {
                Add(nameof(targetType), "Android 第一版只支持 Game。");
            }
            else
            {
                var targetName = ResolveTargetName(request, platform.Value, targetType.Value);
                if (string.IsNullOrWhiteSpace(targetName))
                {
                    Add(nameof(targetType), $"项目未配置 {platform.Value} / {targetType.Value} Target。");
                }
                else if (targetSearchRoots.Count > 0)
                {
                    var requestedTargetFileName = $"{targetName.Trim()}.Target.cs";
                    var exists = targetSearchRoots.Any(root =>
                        Directory.Exists(root) &&
                        Directory.EnumerateFiles(root, requestedTargetFileName, SearchOption.AllDirectories).Any());

                    if (!exists)
                    {
                        Add(nameof(targetType), $"当前项目中不存在 Target {targetName.Trim()}。请在项目配置中改为实际 Target 名称。");
                    }
                }
            }

            if (platform == BuildPlatform.Android)
            {
                ValidateAndroidRequest(request, projectDirectory);
            }
        }

        await ValidateArchiveDirectoryAsync(request.ArchiveRootPath, errors, cancellationToken);
        await ValidateUniqueIdentityAsync(request, existingProjectId, errors, cancellationToken);

        if (!errors.ContainsKey(nameof(request.WorkingCopyPath)) && Directory.Exists(request.WorkingCopyPath))
        {
            await ValidateSvnWorkingCopyAsync(request.WorkingCopyPath, includeSvnStatus, errors, cancellationToken);
        }

        return errors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);

        void ValidateAndroidRequest(UpsertProjectRequest source, string? sourceProjectDirectory)
        {
            if (!(source.AndroidEnabled ?? true))
            {
                Add(nameof(source.AndroidEnabled), "该项目未启用 Android 构建。");
            }

            if (!HasAndroidRuntimeSettings(sourceProjectDirectory))
            {
                Add(nameof(source.AndroidEnabled), "项目未检测到 AndroidRuntimeSettings 配置。请先在项目中启用 Android 平台设置。");
            }

            var sdkRoot = ResolveAndroidSdkRoot();
            if (string.IsNullOrWhiteSpace(sdkRoot) || !Directory.Exists(sdkRoot))
            {
                Add(nameof(platform), "未检测到 Android SDK。请先配置 ANDROID_HOME 或 ANDROID_SDK_ROOT。");
            }
            else
            {
                var licensesDirectory = Path.Combine(sdkRoot, "licenses");
                var licensePath = Path.Combine(licensesDirectory, "android-sdk-license");
                if (!File.Exists(licensePath))
                {
                    Add(nameof(platform), "未检测到 Android SDK License。请先接受 Android SDK License。");
                }

                var buildToolsDirectory = Path.Combine(sdkRoot, "build-tools");
                if (!Directory.Exists(buildToolsDirectory) ||
                    !Directory.EnumerateDirectories(buildToolsDirectory).Any())
                {
                    Add(nameof(platform), "Android SDK 缺少 build-tools。");
                }
            }

            var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (string.IsNullOrWhiteSpace(javaHome) || !Directory.Exists(javaHome))
            {
                Add(nameof(platform), "未检测到 JAVA_HOME。");
            }

            var ndkRoot = ResolveAndroidNdkRoot(sdkRoot);
            if (string.IsNullOrWhiteSpace(ndkRoot) || !Directory.Exists(ndkRoot))
            {
                Add(nameof(platform), "未检测到 Android NDK。请先配置 ANDROID_NDK_ROOT 或 NDKROOT。");
            }
        }

        void AddIf(bool condition, string key, string message)
        {
            if (condition)
            {
                Add(key, message);
            }
        }

        void Add(string key, string message)
        {
            if (!errors.TryGetValue(key, out var list))
            {
                list = new List<string>();
                errors[key] = list;
            }

            if (!list.Contains(message, StringComparer.Ordinal))
            {
                list.Add(message);
            }
        }

        void ValidateTargetField(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value) && value.Any(char.IsWhiteSpace))
            {
                Add(key, "Target 名称不能包含空白字符。");
            }
        }

        void ValidateTargetFile(string key, string? value, IReadOnlyList<string> searchRoots)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var fileName = $"{value.Trim()}.Target.cs";
            var exists = searchRoots.Any(root =>
                Directory.Exists(root) &&
                Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).Any());

            if (!exists)
            {
                Add(key, $"未找到 Target 文件 {fileName}。请确认项目配置中的 Target 名称与 .uproject 实际 Target 一致。");
            }
        }
    }

    private static string ResolveTargetName(
        UpsertProjectRequest request,
        BuildPlatform platform,
        BuildTargetType targetType)
    {
        if (platform == BuildPlatform.Android)
        {
            return request.GameTarget ?? string.Empty;
        }

        return targetType switch
        {
            BuildTargetType.Game => request.GameTarget ?? string.Empty,
            BuildTargetType.Client => request.ClientTarget ?? string.Empty,
            BuildTargetType.Server => request.ServerTarget ?? string.Empty,
            _ => string.Empty
        };
    }

    private static IEnumerable<string> GetTargetSearchRoots(string? projectDirectory)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            yield break;
        }

        var sourceDirectory = Path.Combine(projectDirectory, "Source");
        if (Directory.Exists(sourceDirectory))
        {
            yield return sourceDirectory;
        }

        var intermediateSourceDirectory = Path.Combine(projectDirectory, "Intermediate", "Source");
        if (Directory.Exists(intermediateSourceDirectory))
        {
            yield return intermediateSourceDirectory;
        }
    }

    private static bool HasAndroidRuntimeSettings(string? projectDirectory)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            return false;
        }

        var configDirectory = Path.Combine(projectDirectory, "Config");
        if (!Directory.Exists(configDirectory))
        {
            return false;
        }

        foreach (var filePath in Directory.EnumerateFiles(configDirectory, "*.ini", SearchOption.AllDirectories))
        {
            try
            {
                var content = File.ReadAllText(filePath);
                if (content.Contains("AndroidRuntimeSettings", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
                // Ignore unreadable config fragments and continue.
            }
        }

        return false;
    }

    private static string? ResolveAndroidSdkRoot()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
            Environment.GetEnvironmentVariable("ANDROID_HOME")
        };

        return candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
    }

    private static string? ResolveAndroidNdkRoot(string? sdkRoot)
    {
        var envCandidates = new[]
        {
            Environment.GetEnvironmentVariable("ANDROID_NDK_ROOT"),
            Environment.GetEnvironmentVariable("NDKROOT")
        };

        var envMatch = envCandidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
        if (!string.IsNullOrWhiteSpace(envMatch))
        {
            return envMatch;
        }

        if (string.IsNullOrWhiteSpace(sdkRoot) || !Directory.Exists(sdkRoot))
        {
            return null;
        }

        var ndkBundle = Path.Combine(sdkRoot, "ndk-bundle");
        if (Directory.Exists(ndkBundle))
        {
            return ndkBundle;
        }

        var ndkDirectory = Path.Combine(sdkRoot, "ndk");
        if (!Directory.Exists(ndkDirectory))
        {
            return null;
        }

        return Directory.EnumerateDirectories(ndkDirectory)
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private async Task ValidateUniqueIdentityAsync(
        UpsertProjectRequest request,
        Guid? existingProjectId,
        Dictionary<string, List<string>> errors,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var normalizedProjectKey = ProjectIdentity.EnsureProjectKey(request.ProjectKey);
        var fingerprint = ProjectIdentity.CreateFingerprint(request.WorkingCopyPath, request.UProjectPath, request.EngineRootPath);

        var duplicateKey = await db.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(project =>
                project.ProjectKey == normalizedProjectKey &&
                (!existingProjectId.HasValue || project.Id != existingProjectId.Value),
                cancellationToken);

        if (duplicateKey is not null)
        {
            AddError(nameof(request.ProjectKey), "ProjectKey 已被其他项目占用。");
        }

        var duplicateFingerprint = await db.Projects
            .AsNoTracking()
            .Where(project =>
                project.ProjectFingerprint == fingerprint &&
                (!existingProjectId.HasValue || project.Id != existingProjectId.Value))
            .Select(project => project.Name)
            .Take(2)
            .ToListAsync(cancellationToken);

        if (duplicateFingerprint.Count > 0)
        {
            AddError(nameof(request.WorkingCopyPath), $"相同的工作副本 / uproject / Engine 组合已被项目“{duplicateFingerprint[0]}”使用。");
        }

        void AddError(string key, string message)
        {
            if (!errors.TryGetValue(key, out var list))
            {
                list = new List<string>();
                errors[key] = list;
            }

            if (!list.Contains(message, StringComparer.Ordinal))
            {
                list.Add(message);
            }
        }
    }

    private async Task ValidateArchiveDirectoryAsync(
        string archiveRootPath,
        Dictionary<string, List<string>> errors,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(archiveRootPath))
        {
            return;
        }

        var probeDirectory = Directory.Exists(archiveRootPath)
            ? archiveRootPath
            : Path.GetDirectoryName(archiveRootPath);

        if (string.IsNullOrWhiteSpace(probeDirectory) || !Directory.Exists(probeDirectory))
        {
            AddError(nameof(UpsertProjectRequest.ArchiveRootPath), "归档根目录不存在，且其父目录不可用。");
            return;
        }

        var probeFile = Path.Combine(probeDirectory, $".write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(probeFile, "ok", cancellationToken);
        }
        catch
        {
            AddError(nameof(UpsertProjectRequest.ArchiveRootPath), "归档目录不可写。");
        }
        finally
        {
            try
            {
                if (File.Exists(probeFile))
                {
                    File.Delete(probeFile);
                }
            }
            catch
            {
                // Ignore cleanup failures for the temporary probe file.
            }
        }

        void AddError(string key, string message)
        {
            if (!errors.TryGetValue(key, out var list))
            {
                list = new List<string>();
                errors[key] = list;
            }

            if (!list.Contains(message, StringComparer.Ordinal))
            {
                list.Add(message);
            }
        }
    }

    private async Task ValidateSvnWorkingCopyAsync(
        string workingCopyPath,
        bool includeSvnStatus,
        Dictionary<string, List<string>> errors,
        CancellationToken cancellationToken)
    {
        var svnInfo = await RunProcessAsync(
            "svn",
            ["info", workingCopyPath],
            workingCopyPath,
            cancellationToken);

        if (svnInfo.ExitCode != 0)
        {
            logger.LogInformation("`svn info` failed for working copy {WorkingCopyPath}. Attempting automatic `svn cleanup`.", workingCopyPath);
            await TryCleanupWorkingCopyAsync(workingCopyPath, cancellationToken);

            svnInfo = await RunProcessAsync(
                "svn",
                ["info", workingCopyPath],
                workingCopyPath,
                cancellationToken);

            if (svnInfo.ExitCode != 0)
            {
                AddError(nameof(UpsertProjectRequest.WorkingCopyPath), "SVN 工作副本不可用，`svn info` 执行失败。系统已自动尝试 `svn cleanup`，但未恢复。");
                return;
            }
        }

        if (!includeSvnStatus)
        {
            return;
        }

        var svnStatus = await RunProcessAsync(
            "svn",
            ["status", workingCopyPath],
            workingCopyPath,
            cancellationToken);

        if (svnStatus.ExitCode != 0)
        {
            logger.LogInformation("`svn status` failed for working copy {WorkingCopyPath}. Attempting automatic `svn cleanup`.", workingCopyPath);
            await TryCleanupWorkingCopyAsync(workingCopyPath, cancellationToken);

            svnStatus = await RunProcessAsync(
                "svn",
                ["status", workingCopyPath],
                workingCopyPath,
                cancellationToken);

            if (svnStatus.ExitCode != 0)
            {
                AddError(nameof(UpsertProjectRequest.WorkingCopyPath), "SVN 工作副本状态不可用，`svn status` 执行失败。系统已自动尝试 `svn cleanup`，但未恢复。");
                return;
            }
        }

        var statusLines = ParseSvnStatusLines(svnStatus.Output);

        var lockedStatusLine = statusLines.FirstOrDefault(IsLockedSvnStatusLine);
        if (lockedStatusLine is not null)
        {
            logger.LogInformation("Detected locked SVN working copy {WorkingCopyPath}. Attempting automatic `svn cleanup`.", workingCopyPath);
            await TryCleanupWorkingCopyAsync(workingCopyPath, cancellationToken);

            svnStatus = await RunProcessAsync(
                "svn",
                ["status", workingCopyPath],
                workingCopyPath,
                cancellationToken);

            if (svnStatus.ExitCode != 0)
            {
                AddError(nameof(UpsertProjectRequest.WorkingCopyPath), "SVN 工作副本存在锁，系统已自动尝试 `svn cleanup`，但 `svn status` 仍失败。");
                return;
            }

            statusLines = ParseSvnStatusLines(svnStatus.Output);
        }

        var missingStatusLines = statusLines.Where(IsMissingSvnStatusLine).ToList();
        if (missingStatusLines.Count > 0)
        {
            var repaired = await TryRepairMissingSvnItemsAsync(workingCopyPath, missingStatusLines, cancellationToken);
            if (repaired)
            {
                svnStatus = await RunProcessAsync(
                    "svn",
                    ["status", workingCopyPath],
                    workingCopyPath,
                    cancellationToken);

                if (svnStatus.ExitCode != 0)
                {
                    AddError(nameof(UpsertProjectRequest.WorkingCopyPath), "SVN 工作副本在自动修复缺失文件后，`svn status` 仍执行失败。");
                    return;
                }

                statusLines = ParseSvnStatusLines(svnStatus.Output);
            }
        }

        var invalidStatusLine = statusLines.FirstOrDefault(IsInvalidSvnStatusLine);
        if (invalidStatusLine is not null)
        {
            AddError(nameof(UpsertProjectRequest.WorkingCopyPath), $"SVN 工作副本存在异常状态：{invalidStatusLine}");
        }

        void AddError(string key, string message)
        {
            if (!errors.TryGetValue(key, out var list))
            {
                list = new List<string>();
                errors[key] = list;
            }

            if (!list.Contains(message, StringComparer.Ordinal))
            {
                list.Add(message);
            }
        }
    }

    private static bool IsInvalidSvnStatusLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var first = line[0];
        if (first is 'C' or '!' or '~')
        {
            return true;
        }

        return line.Length > 2 && line[2] == 'L';
    }

    private static bool IsLockedSvnStatusLine(string line)
    {
        return !string.IsNullOrWhiteSpace(line) && line.Length > 2 && line[2] == 'L';
    }

    private static bool IsMissingSvnStatusLine(string line)
    {
        return !string.IsNullOrWhiteSpace(line) && line[0] == '!';
    }

    private static List<string> ParseSvnStatusLines(string output)
    {
        return output
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
    }

    private async Task<bool> TryRepairMissingSvnItemsAsync(
        string workingCopyPath,
        IReadOnlyList<string> missingStatusLines,
        CancellationToken cancellationToken)
    {
        var missingPaths = missingStatusLines
            .Select(TryExtractSvnStatusPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(Path.Combine(workingCopyPath, path!)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (missingPaths.Count == 0)
        {
            return false;
        }

        var autoRepairCandidates = missingPaths
            .Where(ShouldAutoRepairMissingPath)
            .ToList();

        if (autoRepairCandidates.Count == 0)
        {
            logger.LogInformation(
                "Detected missing SVN items for {WorkingCopyPath}, but none matched auto-repair policy: {MissingPaths}",
                workingCopyPath,
                string.Join(", ", missingPaths));
            return false;
        }

        logger.LogInformation(
            "Attempting automatic repair for missing SVN items in {WorkingCopyPath}: {MissingPaths}",
            workingCopyPath,
            string.Join(", ", autoRepairCandidates));

        foreach (var path in autoRepairCandidates)
        {
            await RunProcessAsync("svn", ["revert", path], workingCopyPath, cancellationToken);
            await RunProcessAsync("svn", ["update", path], workingCopyPath, cancellationToken);
        }

        return true;
    }

    private static string? TryExtractSvnStatusPath(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length <= 8)
        {
            return null;
        }

        return line[8..].Trim();
    }

    private static bool ShouldAutoRepairMissingPath(string fullPath)
    {
        var normalizedPath = fullPath.Replace('/', '\\');
        var fileName = Path.GetFileName(normalizedPath);
        var extension = Path.GetExtension(normalizedPath);

        if (normalizedPath.Contains("\\Binaries\\", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.Contains("\\Intermediate\\", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.Contains("\\Saved\\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return extension.Equals(".pdb", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".obj", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".lib", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".exp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".ilk", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".generated.h", StringComparison.OrdinalIgnoreCase);
    }

    private async Task TryCleanupWorkingCopyAsync(string workingCopyPath, CancellationToken cancellationToken)
    {
        var cleanup = await RunProcessAsync(
            "svn",
            ["cleanup", workingCopyPath],
            workingCopyPath,
            cancellationToken);

        if (cleanup.ExitCode == 0)
        {
            logger.LogInformation("Automatic `svn cleanup` succeeded for working copy {WorkingCopyPath}.", workingCopyPath);
            return;
        }

        logger.LogWarning(
            "Automatic `svn cleanup` failed for working copy {WorkingCopyPath}. ExitCode={ExitCode}. Output={Output}",
            workingCopyPath,
            cleanup.ExitCode,
            cleanup.Output);
    }

    private async Task<(int ExitCode, string Output)> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            var output = $"{await stdoutTask}{Environment.NewLine}{await stderrTask}".Trim();
            return (process.ExitCode, output);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to execute validation process {FileName}", fileName);
            return (-1, string.Empty);
        }
    }
}
