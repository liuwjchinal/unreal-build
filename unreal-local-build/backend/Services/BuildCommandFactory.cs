using Backend.Models;

namespace Backend.Services;

public sealed record ProcessCommand(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, string DisplayString);

public static class BuildCommandFactory
{
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

        return new ProcessCommand(
            "cmd.exe",
            arguments,
            Path.GetDirectoryName(runUatPath) ?? project.EngineRootPath,
            $"cmd.exe {string.Join(' ', arguments.Select(QuoteForDisplay))}");
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

        if (build.Pak)
        {
            arguments.Add("-pak");
        }

        arguments.Add(build.IoStore ? "-iostore" : "-skipiostore");
        arguments.AddRange(build.ExtraUatArgs);
        return arguments;
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
    }

    private static string QuoteForDisplay(string value)
    {
        return value.Contains(' ') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : value;
    }
}
