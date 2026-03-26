namespace Backend.Services;

public static class PathDisplay
{
    public static string Mask(string? path, int maxSegments = 2)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "-";
        }

        var normalized = path.Trim();
        var root = Path.GetPathRoot(normalized) ?? string.Empty;
        var segments = normalized[root.Length..]
            .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
        {
            return normalized;
        }

        if (segments.Length <= maxSegments)
        {
            return normalized;
        }

        var visibleSegments = segments[^maxSegments..];
        var prefix = string.IsNullOrWhiteSpace(root) ? "..." : $"{root}...";
        return Path.Combine([prefix, .. visibleSegments]);
    }
}
