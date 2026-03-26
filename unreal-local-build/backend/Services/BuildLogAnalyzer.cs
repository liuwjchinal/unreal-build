namespace Backend.Services;

public sealed class BuildLogAnalyzer
{
    private static readonly string[] ErrorMarkers =
    [
        "AutomationException",
        "PackagingResults: Error",
        "BUILD FAILED",
        "error C",
        "error LNK",
        "ERROR:",
        "Error:"
    ];

    public string ExtractSummary(string logPath, int? exitCode)
    {
        if (!File.Exists(logPath))
        {
            return exitCode.HasValue
                ? AppText.BuildProcessFailed(exitCode.Value)
                : "构建失败，请查看完整日志。";
        }

        try
        {
            var candidateLines = ReadSharedLines(logPath)
                .Where(IsErrorLine)
                .Take(6)
                .ToList();

            if (candidateLines.Count > 0)
            {
                return string.Join(Environment.NewLine, candidateLines);
            }

            var tail = TailShared(logPath, 8);
            if (tail.Count > 0)
            {
                return string.Join(Environment.NewLine, tail);
            }
        }
        catch
        {
            // Fall back to a generic summary if the log is still being flushed.
        }

        return exitCode.HasValue
            ? AppText.BuildProcessFailed(exitCode.Value)
            : "构建失败，请查看完整日志。";
    }

    private static IEnumerable<string> ReadSharedLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private static bool IsErrorLine(string line)
    {
        return ErrorMarkers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> TailShared(string path, int count)
    {
        var queue = new Queue<string>(count);
        foreach (var line in ReadSharedLines(path))
        {
            if (queue.Count == count)
            {
                queue.Dequeue();
            }

            queue.Enqueue(line);
        }

        return queue.ToList();
    }
}
