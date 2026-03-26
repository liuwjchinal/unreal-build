using System.Text;
using Backend.Contracts;

namespace Backend.Services;

public sealed class BuildLogReader
{
    public async Task<BuildLogSnapshotDto> ReadAsync(string path, int tailLines, long totalLinesHint, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new BuildLogSnapshotDto(Array.Empty<string>(), 0, 0, false);
        }

        var effectiveTailLines = Math.Max(1, tailLines);
        var bytes = await ReadTailBytesAsync(path, effectiveTailLines, cancellationToken);
        var text = Encoding.UTF8.GetString(bytes);
        var lines = text
            .Split(["\r\n", "\n"], StringSplitOptions.None)
            .Where(static line => !string.IsNullOrEmpty(line))
            .ToArray();

        if (lines.Length > effectiveTailLines)
        {
            lines = lines[^effectiveTailLines..];
        }

        var totalLines = Math.Max(totalLinesHint, lines.Length);
        return new BuildLogSnapshotDto(
            lines,
            lines.Length,
            totalLines,
            totalLines > lines.Length);
    }

    private static async Task<byte[]> ReadTailBytesAsync(string path, int tailLines, CancellationToken cancellationToken)
    {
        const int chunkSize = 4096;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length == 0)
        {
            return Array.Empty<byte>();
        }

        long startOffset = 0;
        var remaining = stream.Length;
        var newlineCount = 0;
        var buffer = new byte[chunkSize];

        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bytesToRead = (int)Math.Min(chunkSize, remaining);
            remaining -= bytesToRead;
            stream.Seek(remaining, SeekOrigin.Begin);

            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, bytesToRead), cancellationToken);
            for (var index = bytesRead - 1; index >= 0; index--)
            {
                if (buffer[index] != (byte)'\n')
                {
                    continue;
                }

                newlineCount++;
                if (newlineCount > tailLines)
                {
                    startOffset = remaining + index + 1;
                    remaining = 0;
                    break;
                }
            }
        }

        stream.Seek(startOffset, SeekOrigin.Begin);
        var byteCount = (int)(stream.Length - startOffset);
        var result = new byte[byteCount];
        _ = await stream.ReadAsync(result, cancellationToken);
        return result;
    }
}
