using System.Diagnostics;
using Backend.Contracts;
using Backend.Data;
using Backend.Models;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

public sealed class ProjectValidator(IDbContextFactory<BuildDbContext> dbFactory, ILogger<ProjectValidator> logger)
{
    public Task<Dictionary<string, string[]>> ValidateProjectAsync(
        UpsertProjectRequest request,
        Guid? existingProjectId,
        CancellationToken cancellationToken)
    {
        return ValidateProjectCoreAsync(request, existingProjectId, includeSvnStatus: false, targetType: null, buildConfiguration: null, cancellationToken);
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
            project.AllowedBuildConfigurations,
            project.DefaultExtraUatArgs);

        return ValidateProjectCoreAsync(
            normalizedRequest,
            project.Id,
            includeSvnStatus: true,
            request.TargetType,
            request.BuildConfiguration,
            cancellationToken);
    }

    private async Task<Dictionary<string, string[]>> ValidateProjectCoreAsync(
        UpsertProjectRequest request,
        Guid? existingProjectId,
        bool includeSvnStatus,
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

        var sourceDirectory = string.IsNullOrWhiteSpace(request.UProjectPath)
            ? null
            : Path.Combine(Path.GetDirectoryName(request.UProjectPath) ?? string.Empty, "Source");

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
                    Add(nameof(request.EngineRootPath), "未找到 Engine\\Build\\BatchFiles\\RunUAT.bat。");
                }
            }
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

        if (Directory.Exists(sourceDirectory))
        {
            ValidateTargetFile(nameof(request.GameTarget), request.GameTarget, sourceDirectory);
            ValidateTargetFile(nameof(request.ClientTarget), request.ClientTarget, sourceDirectory);
            ValidateTargetFile(nameof(request.ServerTarget), request.ServerTarget, sourceDirectory);
        }

        if (targetType.HasValue)
        {
            var targetName = targetType.Value switch
            {
                BuildTargetType.Game => request.GameTarget,
                BuildTargetType.Client => request.ClientTarget,
                BuildTargetType.Server => request.ServerTarget,
                _ => null
            };

            if (string.IsNullOrWhiteSpace(targetName))
            {
                Add(nameof(targetType), AppText.TargetNotConfigured(targetType.Value.ToString()));
            }
        }

        await ValidateArchiveDirectoryAsync(request.ArchiveRootPath, errors, cancellationToken);
        await ValidateUniqueIdentityAsync(request, existingProjectId, errors, cancellationToken);

        if (!errors.ContainsKey(nameof(request.WorkingCopyPath)) && Directory.Exists(request.WorkingCopyPath))
        {
            await ValidateSvnWorkingCopyAsync(request.WorkingCopyPath, includeSvnStatus, errors, cancellationToken);
        }

        return errors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);

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

        void ValidateTargetFile(string key, string? value, string sourceRoot)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var fileName = $"{value.Trim()}.Target.cs";
            var exists = Directory.EnumerateFiles(sourceRoot, fileName, SearchOption.AllDirectories).Any();
            if (!exists)
            {
                Add(key, $"未在 Source 目录下找到 {fileName}。");
            }
        }
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
            AddError(nameof(request.ProjectKey), "ProjectKey 已被其它项目占用。");
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
            AddError(nameof(request.WorkingCopyPath), $"相同的工作副本 / uproject / Engine 组合已被项目 “{duplicateFingerprint[0]}” 使用。");
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

    private async Task ValidateArchiveDirectoryAsync(string archiveRootPath, Dictionary<string, List<string>> errors, CancellationToken cancellationToken)
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
            AddError(nameof(UpsertProjectRequest.WorkingCopyPath), "SVN 工作副本不可用，`svn info` 执行失败。");
            return;
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
            AddError(nameof(UpsertProjectRequest.WorkingCopyPath), "SVN 工作副本状态不可用，`svn status` 执行失败。");
            return;
        }

        var invalidStatusLine = svnStatus.Output
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(IsInvalidSvnStatusLine);

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
