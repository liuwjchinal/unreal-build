using Backend.Contracts;
using Backend.Data;
using Backend.Models;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

public sealed class BuildScheduleValidator(IDbContextFactory<BuildDbContext> dbFactory)
{
    public async Task<Dictionary<string, string[]>> ValidateAsync(
        UpsertBuildScheduleRequest request,
        Guid? existingScheduleId,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        AddIf(string.IsNullOrWhiteSpace(request.Name), nameof(request.Name), "任务名称不能为空。");
        AddIf(string.IsNullOrWhiteSpace(request.TimeOfDayLocal), nameof(request.TimeOfDayLocal), "触发时间不能为空。");
        AddIf(string.IsNullOrWhiteSpace(request.BuildConfiguration), nameof(request.BuildConfiguration), "构建配置不能为空。");

        if (!string.IsNullOrWhiteSpace(request.TimeOfDayLocal) &&
            !TimeOnly.TryParseExact(request.TimeOfDayLocal.Trim(), "HH:mm", out _))
        {
            Add(nameof(request.TimeOfDayLocal), "触发时间必须使用 24 小时制 HH:mm 格式。");
        }

        if (!BuildCommandFactory.SupportsTargetType(request.Platform, request.TargetType))
        {
            Add(nameof(request.TargetType), "Android 定时任务只支持 Game。");
        }

        if (request.ScopeType == BuildScheduleScopeType.SingleProject)
        {
            if (!request.ProjectId.HasValue)
            {
                Add(nameof(request.ProjectId), "单项目定时任务必须选择项目。");
            }
            else
            {
                await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
                var project = await db.Projects
                    .AsNoTracking()
                    .FirstOrDefaultAsync(item => item.Id == request.ProjectId.Value, cancellationToken);

                if (project is null)
                {
                    Add(nameof(request.ProjectId), "未找到对应项目。");
                }
                else
                {
                    ValidateProjectCompatibility(project);
                }
            }
        }
        else if (request.ProjectId.HasValue)
        {
            Add(nameof(request.ProjectId), "“所有项目”任务不能绑定单个项目。");
        }

        return errors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);

        void ValidateProjectCompatibility(ProjectConfig project)
        {
            if (!project.AllowedBuildConfigurations.Contains(request.BuildConfiguration.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                Add(nameof(request.BuildConfiguration), "该项目不允许当前构建配置。");
            }

            if (request.Platform == BuildPlatform.Android && !project.AndroidEnabled)
            {
                Add(nameof(request.Platform), "该项目未启用 Android 构建。");
            }

            var targetName = BuildCommandFactory.ResolveTargetName(project, request.Platform, request.TargetType);
            if (string.IsNullOrWhiteSpace(targetName))
            {
                Add(nameof(request.TargetType), $"项目未配置 {request.Platform} / {request.TargetType} Target。");
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
    }
}
