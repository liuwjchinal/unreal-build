using Backend.Models;

namespace Backend.Services;

public sealed record ProcessCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string DisplayString,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null);

public static class BuildCommandFactory
{
    private const string AndroidMaxChunkSizeBytes = "900000000";
    private const string AndroidMaxIoStorePartitionSizeMb = "900";
    private const string AndroidOverflowObbFileLimit = "16";
    private const string AndroidExternalDataObbFilter = "-.../Content/Paks/...";
    private const string AndroidExternalFilesObbFilterArgument = "-ini:Engine:[/Script/AndroidRuntimeSettings.AndroidRuntimeSettings]:+ObbFilters=-.../Content/Paks/...";

    public static ProcessCommand CreateSvnCommand(ProjectConfig project, BuildRecord build)
    {
        var arguments = new List<string>
        {
            "update",
            "-r",
            NormalizeRevision(build.Revision)
        };

        return new ProcessCommand(
            "svn",
            arguments,
            project.WorkingCopyPath,
            $"svn {string.Join(' ', arguments.Select(QuoteForDisplay))}");
    }

    public static ProcessCommand CreateUatCommand(ProjectConfig project, BuildRecord build)
    {
        if (string.IsNullOrWhiteSpace(build.ArchiveDirectoryPath))
        {
            throw new InvalidOperationException($"Build {build.Id} archive directory is empty.");
        }

        var runUatPath = Path.Combine(project.EngineRootPath, "Engine", "Build", "BatchFiles", "RunUAT.bat");
        var arguments = BuildBaseUatArguments(project, build, includeArchiveDirectory: true);

        IReadOnlyDictionary<string, string>? environmentVariables = null;
        if (build.Platform == BuildPlatform.Android)
        {
            environmentVariables = AndroidToolchain.BuildProcessEnvironmentOverrides();
        }

        return new ProcessCommand(
            "cmd.exe",
            arguments,
            Path.GetDirectoryName(runUatPath) ?? project.EngineRootPath,
            $"cmd.exe {string.Join(' ', arguments.Select(QuoteForDisplay))}",
            environmentVariables);
    }

    public static string ResolveTargetName(ProjectConfig project, BuildPlatform platform, BuildTargetType targetType)
    {
        return platform switch
        {
            BuildPlatform.Windows => targetType switch
            {
                BuildTargetType.Game => project.GameTarget ?? string.Empty,
                BuildTargetType.Client => project.ClientTarget ?? string.Empty,
                BuildTargetType.Server => project.ServerTarget ?? string.Empty,
                _ => string.Empty
            },
            BuildPlatform.Android => project.GameTarget ?? string.Empty,
            BuildPlatform.OpenHarmony => project.GameTarget ?? string.Empty,
            _ => string.Empty
        };
    }

    public static string NormalizeRevision(string revision)
    {
        return string.IsNullOrWhiteSpace(revision) ? "HEAD" : revision.Trim();
    }

    public static string CreateSvnPreview(BuildRecord build)
    {
        return $"svn update -r {NormalizeRevision(build.Revision)}";
    }

    public static string CreateUatPreview(BuildRecord build)
    {
        return string.Join(' ', BuildBaseUatArguments(null, build, includeArchiveDirectory: false).Select(QuoteForDisplay));
    }

    public static bool SupportsTargetType(BuildPlatform platform, BuildTargetType targetType)
    {
        return platform == BuildPlatform.Windows || targetType == BuildTargetType.Game;
    }

    public static string GetDefaultAndroidTextureFlavor() => "ASTC";

    public static bool ContainsUbtArgs(IEnumerable<string>? args)
    {
        return (args ?? Array.Empty<string>()).Any(arg =>
            arg.TrimStart().StartsWith("-ubtargs", StringComparison.OrdinalIgnoreCase));
    }

    public static bool ContainsNoUba(IEnumerable<string>? args)
    {
        return (args ?? Array.Empty<string>()).Any(arg =>
            arg.Contains("-NoUBA", StringComparison.OrdinalIgnoreCase));
    }

    public static bool ContainsAndroidExternalFilesIoStoreConflictArgs(IEnumerable<string>? args)
    {
        return (args ?? Array.Empty<string>()).Any(IsAndroidExternalFilesIoStoreConflictArg);
    }

    private static List<string> BuildBaseUatArguments(ProjectConfig? project, BuildRecord build, bool includeArchiveDirectory)
    {
        var arguments = new List<string>();
        if (project is not null)
        {
            var runUatPath = Path.Combine(project.EngineRootPath, "Engine", "Build", "BatchFiles", "RunUAT.bat");
            arguments.Add("/c");
            arguments.Add(runUatPath);
        }
        else
        {
            arguments.Add("RunUAT.bat");
        }

        arguments.Add("BuildCookRun");

        if (project is not null)
        {
            arguments.Add($"-project={project.UProjectPath}");
        }

        arguments.Add("-build");
        arguments.Add("-cook");
        arguments.Add("-stage");
        arguments.Add("-package");
        arguments.Add("-archive");
        if (includeArchiveDirectory)
        {
            arguments.Add($"-archivedirectory={build.ArchiveDirectoryPath}");
        }

        arguments.Add("-nop4");
        arguments.Add("-unattended");

        AppendPlatformArguments(arguments, project, build);

        if (build.Clean)
        {
            arguments.Add("-clean");
        }

        if (build.Pak || RequiresAndroidExternalContainers(build))
        {
            arguments.Add("-pak");
        }

        arguments.Add(build.IoStore || RequiresAndroidExternalContainers(build) ? "-iostore" : "-skipiostore");
        AppendUbtArgs(arguments, build);
        arguments.AddRange(ResolveExtraUatArgs(build));
        return arguments;
    }

    private static bool RequiresAndroidExternalContainers(BuildRecord build)
    {
        return build.Platform == BuildPlatform.Android &&
               build.AndroidPackagingMode == AndroidPackagingMode.ExternalFilesIoStore;
    }

    private static IEnumerable<string> ResolveExtraUatArgs(BuildRecord build)
    {
        if (!RequiresAndroidExternalContainers(build))
        {
            return build.ExtraUatArgs;
        }

        return build.ExtraUatArgs.Where(arg => !IsAndroidExternalFilesIoStoreConflictArg(arg));
    }

    private static bool IsAndroidExternalFilesIoStoreConflictArg(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return false;
        }

        var normalized = arg.Trim();
        if (normalized.Equals("-skipiostore", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("-skippak", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("-forcepackagedata", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (TryGetAndroidIniOverrideValue(normalized, "bPackageDataInsideApk", out var packageDataInsideApkValue))
        {
            return !string.Equals(packageDataInsideApkValue, "False", StringComparison.OrdinalIgnoreCase);
        }

        if (TryGetAndroidIniOverrideValue(normalized, "bUseExternalFilesDir", out var useExternalFilesDirValue))
        {
            return !string.Equals(useExternalFilesDirValue, "True", StringComparison.OrdinalIgnoreCase);
        }

        if (TryGetAndroidIniOverrideValue(normalized, "bGenerateChunks", out var generateChunksValue))
        {
            return !string.Equals(generateChunksValue, "True", StringComparison.OrdinalIgnoreCase);
        }

        if (TryGetAndroidIniOverrideValue(normalized, "bGenerateNoChunks", out var generateNoChunksValue))
        {
            return !string.Equals(generateNoChunksValue, "False", StringComparison.OrdinalIgnoreCase);
        }

        if (TryGetAndroidIniOverrideValue(normalized, "MaxChunkSize", out var maxChunkSizeValue))
        {
            return !string.Equals(maxChunkSizeValue, AndroidMaxChunkSizeBytes, StringComparison.OrdinalIgnoreCase);
        }

        if (TryGetAndroidIniOverrideValue(normalized, "MaxIoStorePartitionSizeMB", out var maxIoStorePartitionSizeValue))
        {
            return !string.Equals(maxIoStorePartitionSizeValue, AndroidMaxIoStorePartitionSizeMb, StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(normalized, AndroidExternalFilesObbFilterArgument, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (normalized.Contains("ObbFilters", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool TryGetAndroidIniOverrideValue(string arg, string key, out string value)
    {
        var marker = $":{key}=";
        var index = arg.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            marker = $"{key}=";
            index = arg.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                value = string.Empty;
                return false;
            }
        }

        value = arg[(index + marker.Length)..].Trim();
        if ((value.StartsWith("\"", StringComparison.Ordinal) && value.EndsWith("\"", StringComparison.Ordinal)) ||
            (value.StartsWith("'", StringComparison.Ordinal) && value.EndsWith("'", StringComparison.Ordinal)))
        {
            value = value[1..^1].Trim();
        }

        return true;
    }

    private static void AppendUbtArgs(List<string> arguments, BuildRecord build)
    {
        if (build.BuildAccelerator != BuildAccelerator.Uba)
        {
            return;
        }

        var ubtArgs = new List<string>
        {
            "-UBA",
            "-UBAPrintSummary"
        };

        if (build.UbaRemoteEnabled)
        {
            ubtArgs.Add($"-UBAHost={ResolveUbaListenHost(build)}");
            if (build.UbaPort.HasValue)
            {
                ubtArgs.Add($"-UBAPort={build.UbaPort.Value}");
            }

            ubtArgs.Add($"-UBAStoreCapacityGb={ResolveUbaStoreCapacityGb(build)}");
        }

        ubtArgs.Add($"-UBAMaxWorkers={ResolveUbaMaxWorkers(build)}");
        arguments.Add($"-ubtargs=\"{string.Join(' ', ubtArgs)}\"");
    }

    private static string ResolveUbaListenHost(BuildRecord build)
    {
        return string.IsNullOrWhiteSpace(build.UbaListenHost) ? "0.0.0.0" : build.UbaListenHost.Trim();
    }

    private static int ResolveUbaStoreCapacityGb(BuildRecord build)
    {
        return Math.Max(1, build.UbaAgentStoreCapacityGb ?? 40);
    }

    private static int ResolveUbaMaxWorkers(BuildRecord build)
    {
        return Math.Max(1, build.UbaMaxWorkers ?? 4);
    }

    private static void AppendPlatformArguments(List<string> arguments, ProjectConfig? project, BuildRecord build)
    {
        switch (build.Platform)
        {
            case BuildPlatform.Windows:
                AppendWindowsArguments(arguments, build);
                return;
            case BuildPlatform.Android:
                AppendAndroidArguments(arguments, project, build);
                return;
            case BuildPlatform.OpenHarmony:
                AppendOpenHarmonyArguments(arguments, build);
                return;
            default:
                throw new NotSupportedException($"Unsupported build platform {build.Platform}.");
        }
    }

    private static void AppendWindowsArguments(List<string> arguments, BuildRecord build)
    {
        switch (build.TargetType)
        {
            case BuildTargetType.Game:
                arguments.Add("-targetplatform=Win64");
                arguments.Add($"-clientconfig={build.BuildConfiguration}");
                arguments.Add($"-target={build.TargetName}");
                break;
            case BuildTargetType.Client:
                arguments.Add("-targetplatform=Win64");
                arguments.Add("-client");
                arguments.Add($"-clientconfig={build.BuildConfiguration}");
                arguments.Add($"-target={build.TargetName}");
                break;
            case BuildTargetType.Server:
                arguments.Add("-server");
                arguments.Add("-noclient");
                arguments.Add("-servertargetplatform=Win64");
                arguments.Add($"-serverconfig={build.BuildConfiguration}");
                arguments.Add($"-target={build.TargetName}");
                break;
            default:
                throw new NotSupportedException($"Unsupported Windows target type {build.TargetType}.");
        }
    }

    private static void AppendAndroidArguments(List<string> arguments, ProjectConfig? project, BuildRecord build)
    {
        if (build.TargetType != BuildTargetType.Game)
        {
            throw new InvalidOperationException("Android builds only support the Game target.");
        }

        var flavor = project?.AndroidTextureFlavor;
        if (string.IsNullOrWhiteSpace(flavor))
        {
            flavor = GetDefaultAndroidTextureFlavor();
        }

        arguments.Add("-targetplatform=Android");
        arguments.Add($"-cookflavor={flavor.ToUpperInvariant()}");
        arguments.Add($"-clientconfig={build.BuildConfiguration}");
        arguments.Add($"-target={build.TargetName}");
        arguments.Add("-manifests");
        AppendAndroidPackagingOverrides(arguments, build.AndroidPackagingMode);
    }

    private static void AppendAndroidPackagingOverrides(
        List<string> arguments,
        AndroidPackagingMode packagingMode)
    {
        AddGameIniOverride("bGenerateChunks", "True");
        AddGameIniOverride("bGenerateNoChunks", "False");
        AddGameIniOverride("MaxChunkSize", AndroidMaxChunkSizeBytes);
        AddGameIniOverride("MaxIoStorePartitionSizeMB", AndroidMaxIoStorePartitionSizeMb);

        switch (packagingMode)
        {
            case AndroidPackagingMode.ExternalFilesIoStore:
                AddEngineIniOverride("bPackageDataInsideApk", "False");
                AddEngineIniOverride("bUseExternalFilesDir", "True");
                AddEngineIniOverride("+ObbFilters", AndroidExternalDataObbFilter);
                break;
            case AndroidPackagingMode.SplitObb:
                AddEngineIniOverride("bPackageDataInsideApk", "False");
                AddEngineIniOverride("bUseExternalFilesDir", "False");
                AddEngineIniOverride("bForceSmallOBBFiles", "True");
                AddEngineIniOverride("bAllowLargeOBBFiles", "False");
                AddEngineIniOverride("bAllowPatchOBBFile", "True");
                AddEngineIniOverride("bAllowOverflowOBBFiles", "True");
                AddEngineIniOverride("OverflowOBBFileLimit", AndroidOverflowObbFileLimit);
                break;
            case AndroidPackagingMode.DataInsideApk:
                AddEngineIniOverride("bPackageDataInsideApk", "True");
                AddEngineIniOverride("bUseExternalFilesDir", "False");
                break;
            default:
                throw new NotSupportedException($"Unsupported Android packaging mode {packagingMode}.");
        }

        void AddGameIniOverride(string key, string value)
        {
            arguments.Add($"-ini:Game:[/Script/UnrealEd.ProjectPackagingSettings]:{key}={value}");
        }

        void AddEngineIniOverride(string key, string value)
        {
            arguments.Add($"-ini:Engine:[/Script/AndroidRuntimeSettings.AndroidRuntimeSettings]:{key}={value}");
        }
    }

    private static void AppendOpenHarmonyArguments(List<string> arguments, BuildRecord build)
    {
        if (build.TargetType != BuildTargetType.Game)
        {
            throw new InvalidOperationException("OpenHarmony builds only support the Game target.");
        }

        arguments.Add("-platform=OpenHarmony");
        arguments.Add($"-clientconfig={build.BuildConfiguration}");
        arguments.Add($"-target={build.TargetName}");
    }

    private static string QuoteForDisplay(string value)
    {
        return value.Contains(' ') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : value;
    }
}
