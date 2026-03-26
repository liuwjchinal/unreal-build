namespace Backend.Services;

public static class AppText
{
    public const string WaitingToRun = "等待执行";
    public const string SyncingSource = "正在更新 SVN 工作副本";
    public const string WaitingForUatSlot = "正在等待 Unreal 打包槽位";
    public const string RunningBuildCookRun = "正在执行 Unreal BuildCookRun";
    public const string ZippingArtifacts = "正在压缩产物";
    public const string BuildCompleted = "构建完成";
    public const string BuildFailed = "构建失败";
    public const string BuildCanceled = "已取消";
    public const string BuildInterrupted = "构建已中断";
    public const string UnexpectedExecutorFailure = "后台执行异常";
    public const string ServiceRestartInterrupted = "服务重启，运行中的任务已标记为中断。";
    public const string ServiceStoppingInterrupted = "服务停止，构建已中断。";
    public const string UserCanceledBuild = "用户手动取消构建。";
    public const string UserCanceledQueuedBuild = "用户在排队阶段取消了该构建。";
    public const string NoProjectsImported = "导入列表不能为空。";
    public const string ActiveBuildsPreventDelete = "该项目仍有排队中或运行中的构建任务，无法删除。";
    public const string MissingBuildId = "缺少构建 ID。";
    public const string BuildNotFound = "未找到该构建。";
    public const string LoadingBuildDetail = "正在加载构建详情...";
    public const string LoadingProjects = "正在加载项目...";
    public const string LoadingBuilds = "正在加载构建列表...";
    public const string NoProjects = "当前还没有项目配置。";
    public const string NoBuilds = "还没有构建记录。";
    public const string NoLogOutput = "暂无日志输出。";
    public const string DuplicateProjectKeyInImportFile = "导入文件中存在重复的 ProjectKey。";
    public const string DuplicateProjectFingerprintInImportFile = "导入文件中存在重复的项目路径指纹。";
    public const string MultipleProjectsShareFingerprint = "当前系统中存在多个相同路径指纹的项目，无法自动匹配。";
    public const string SameNameDifferentPathConflict = "存在同名但不同路径的项目，请使用导出文件中的 ProjectKey 或先手动处理冲突。";
    public const string UatSingleInstanceConflict = "检测到 Unreal AutomationTool 已在运行。RunUAT 在同一台机器上默认只允许单实例执行，因此多个打包任务不能并发进入 UAT 阶段。";

    public static string QueueWaiting(int position) => $"等待执行，当前队列位置约为 {position}";

    public static string SvnFailed(int exitCode) => $"SVN 更新失败，退出码 {exitCode}。";

    public static string BuildProcessFailed(int exitCode) => $"构建失败，进程退出码 {exitCode}。";

    public static string BuildFailedWithLogHint(int exitCode) => $"构建失败，退出码 {exitCode}。请查看完整日志。";

    public static string TargetNotConfigured(string targetType) => $"项目未配置 {targetType} Target 名称。";

    public static string ImportConflict(string projectName) => $"项目“{projectName}”无法安全导入。";

    public static string ImportValidationFailed(string projectName) => $"项目“{projectName}”校验失败。";
}
