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
        var arguments = new List<string>
        {
            "/c",
            runUatPath,
            "BuildCookRun",
            $"-project={project.UProjectPath}",
            "-build",
            "-cook",
            "-stage",
            "-package",
            "-archive",
            $"-archivedirectory={build.ArchiveDirectoryPath}",
            "-nop4",
            "-unattended"
        };

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
        }

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

        return new ProcessCommand(
            "cmd.exe",
            arguments,
            Path.GetDirectoryName(runUatPath) ?? project.EngineRootPath,
            $"cmd.exe {string.Join(' ', arguments.Select(QuoteForDisplay))}");
    }

    public static string ResolveTargetName(ProjectConfig project, BuildTargetType targetType)
    {
        return targetType switch
        {
            BuildTargetType.Game => project.GameTarget ?? string.Empty,
            BuildTargetType.Client => project.ClientTarget ?? string.Empty,
            BuildTargetType.Server => project.ServerTarget ?? string.Empty,
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
        var arguments = new List<string>
        {
            "RunUAT.bat",
            "BuildCookRun",
            "-build",
            "-cook",
            "-stage",
            "-package",
            "-archive",
            "-nop4",
            "-unattended"
        };

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
        }

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
        return string.Join(' ', arguments);
    }

    private static string QuoteForDisplay(string value)
    {
        return value.Contains(' ') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : value;
    }
}
