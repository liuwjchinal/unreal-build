namespace Backend.Services;

public static class ProjectIdentity
{
    public static string EnsureProjectKey(string? projectKey)
    {
        if (!string.IsNullOrWhiteSpace(projectKey))
        {
            return projectKey.Trim();
        }

        return Guid.NewGuid().ToString("N");
    }

    public static string CreateFingerprint(string workingCopyPath, string uProjectPath, string engineRootPath)
    {
        return string.Join(
            "|",
            NormalizePath(workingCopyPath),
            NormalizePath(uProjectPath),
            NormalizePath(engineRootPath));
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path)
                .Trim()
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();
        }
        catch
        {
            return path.Trim()
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();
        }
    }
}
