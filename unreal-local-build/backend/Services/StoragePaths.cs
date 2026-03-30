using System.Text;
using Backend.Models;
using Backend.Options;

namespace Backend.Services;

public sealed class StoragePaths
{
    private StoragePaths(string storageRootPath)
    {
        StorageRootPath = storageRootPath;
        DatabasePath = Path.Combine(StorageRootPath, "unreal-build.db");
        BuildsRootPath = Path.Combine(StorageRootPath, "builds");
    }

    public string StorageRootPath { get; }

    public string DatabasePath { get; }

    public string BuildsRootPath { get; }

    public static StoragePaths Create(AppOptions options, string contentRootPath)
    {
        var storageRoot = Path.IsPathRooted(options.StorageRoot)
            ? options.StorageRoot
            : Path.GetFullPath(Path.Combine(contentRootPath, options.StorageRoot));

        return new StoragePaths(storageRoot);
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(StorageRootPath);
        Directory.CreateDirectory(BuildsRootPath);
    }

    public long GetBuildCacheSizeBytes()
    {
        return GetDirectorySize(BuildsRootPath);
    }

    public static long GetDirectorySize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return 0;
        }

        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Select(filePath =>
                {
                    try
                    {
                        return new FileInfo(filePath).Length;
                    }
                    catch
                    {
                        return 0L;
                    }
                })
                .Sum();
        }
        catch
        {
            return 0;
        }
    }

    public string ResolveBuildRoot(Guid buildId) => Path.Combine(BuildsRootPath, buildId.ToString("N"));

    public string ResolveLogPath(Guid buildId) => Path.Combine(ResolveBuildRoot(buildId), "build.log");

    public string ResolveZipPath(BuildRecord build, DateTimeOffset timestampUtc)
    {
        return Path.Combine(ResolveBuildRoot(build.Id), $"{BuildArtifactBaseName(build, timestampUtc)}.zip");
    }

    public string ResolveAutomationToolTrackerPath(Guid buildId) => Path.Combine(ResolveBuildRoot(buildId), "uat-process.json");

    public string ResolveArchiveDirectory(ProjectConfig project, BuildRecord build, DateTimeOffset timestampUtc)
    {
        var buildFolderName = $"{BuildArtifactBaseName(build, timestampUtc)}-{build.Id.ToString("N")[..8]}";
        return Path.Combine(project.ArchiveRootPath, buildFolderName);
    }

    public string ResolveArchiveDirectoryWithFallback(ProjectConfig project, BuildRecord build, DateTimeOffset timestampUtc)
    {
        var preferredRoot = project.ArchiveRootPath?.Trim();
        if (!string.IsNullOrWhiteSpace(preferredRoot))
        {
            return ResolveArchiveDirectory(project, build, timestampUtc);
        }

        return Path.Combine(ResolveBuildRoot(build.Id), "archive");
    }

    public static string FormatSvnRevision(string? revision)
    {
        var normalized = string.IsNullOrWhiteSpace(revision) ? "HEAD" : revision.Trim();
        if (normalized.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            return "HEAD";
        }

        return normalized.StartsWith("r", StringComparison.OrdinalIgnoreCase) ? normalized : $"r{normalized}";
    }

    private static string BuildArtifactBaseName(BuildRecord build, DateTimeOffset timestampUtc)
    {
        var safeProjectName = MakeSafePathSegment(build.Project?.Name ?? "Build");
        var safePlatform = MakeSafePathSegment(build.Platform.ToString());
        var safeConfiguration = MakeSafePathSegment(build.BuildConfiguration);
        var safeTargetType = MakeSafePathSegment(build.TargetType.ToString());
        var safeRevision = MakeSafePathSegment(FormatSvnRevision(build.Revision));
        return $"{timestampUtc:yyyyMMdd-HHmmss}-{safeProjectName}-{safePlatform}-{safeConfiguration}-{safeTargetType}-{safeRevision}";
    }

    private static string MakeSafePathSegment(string input)
    {
        var invalidChars = Path.GetInvalidFileNameChars().ToHashSet();
        var builder = new StringBuilder(input.Length);
        foreach (var character in input)
        {
            builder.Append(invalidChars.Contains(character) ? '_' : character);
        }

        return builder.ToString().Trim();
    }
}
