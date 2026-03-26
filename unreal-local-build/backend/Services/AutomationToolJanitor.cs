using System.Diagnostics;
using System.Text.Json;
using Backend.Data;
using Backend.Models;
using Backend.Options;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

public sealed class AutomationToolJanitor(
    IDbContextFactory<BuildDbContext> dbFactory,
    StoragePaths storagePaths,
    AppOptions appOptions,
    ILogger<AutomationToolJanitor> logger)
{
    private const string TrackedOnlyMode = "TrackedOnly";
    private const string AnyWhenIdleMode = "AnyWhenIdle";

    public async Task CleanupIfSystemIdleAsync(string reason, CancellationToken cancellationToken)
    {
        if (!appOptions.AutomationToolCleanupEnabled)
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var hasRunningBuilds = await db.Builds
            .AsNoTracking()
            .AnyAsync(build => build.Status == BuildStatus.Running, cancellationToken);

        if (hasRunningBuilds)
        {
            return;
        }

        if (string.Equals(appOptions.AutomationToolCleanupMode, AnyWhenIdleMode, StringComparison.OrdinalIgnoreCase))
        {
            await CleanupAnyAutomationToolProcessesAsync(reason, cancellationToken);
            return;
        }

        await CleanupTrackedAutomationToolProcessesAsync(reason, cancellationToken);
    }

    public async Task TrackOwnedProcessAsync(
        Guid buildId,
        ProcessCommand command,
        int processId,
        CancellationToken cancellationToken)
    {
        if (!ShouldTrack(command))
        {
            return;
        }

        var tracker = new AutomationToolProcessTracker(
            buildId,
            processId,
            Path.GetFileName(command.FileName),
            command.DisplayString,
            DateTimeOffset.UtcNow);

        var trackerPath = storagePaths.ResolveAutomationToolTrackerPath(buildId);
        Directory.CreateDirectory(Path.GetDirectoryName(trackerPath)!);
        await File.WriteAllTextAsync(
            trackerPath,
            JsonSerializer.Serialize(tracker, TrackerJsonOptions),
            cancellationToken);
    }

    public async Task ClearOwnedProcessAsync(Guid buildId, CancellationToken cancellationToken)
    {
        var trackerPath = storagePaths.ResolveAutomationToolTrackerPath(buildId);
        if (!File.Exists(trackerPath))
        {
            return;
        }

        try
        {
            File.Delete(trackerPath);
        }
        catch (IOException) when (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(50, cancellationToken);
            if (File.Exists(trackerPath))
            {
                File.Delete(trackerPath);
            }
        }
    }

    private async Task CleanupTrackedAutomationToolProcessesAsync(string reason, CancellationToken cancellationToken)
    {
        var trackerPaths = Directory.Exists(storagePaths.BuildsRootPath)
            ? Directory.GetFiles(storagePaths.BuildsRootPath, "uat-process.json", SearchOption.AllDirectories)
            : Array.Empty<string>();

        var cleanedPids = new List<int>();
        foreach (var trackerPath in trackerPaths)
        {
            var tracker = await ReadTrackerAsync(trackerPath, cancellationToken);
            if (tracker is null)
            {
                TryDeleteTracker(trackerPath);
                continue;
            }

            var process = TryGetProcess(tracker.ProcessId);
            if (process is null)
            {
                TryDeleteTracker(trackerPath);
                continue;
            }

            var commandLine = await QueryCommandLineAsync(tracker.ProcessId, cancellationToken);
            if (!LooksLikeAutomationToolCommand(commandLine))
            {
                logger.LogWarning(
                    "Skipped tracked AutomationTool cleanup for PID {Pid} because the current command line no longer matches UAT. Reason: {Reason}",
                    tracker.ProcessId,
                    reason);
                TryDeleteTracker(trackerPath);
                continue;
            }

            if (await KillProcessTreeAsync(tracker.ProcessId, cancellationToken))
            {
                cleanedPids.Add(tracker.ProcessId);
            }

            TryDeleteTracker(trackerPath);
        }

        if (cleanedPids.Count > 0)
        {
            logger.LogWarning(
                "Cleaned tracked AutomationTool process tree(s) {Pids}. Reason: {Reason}",
                string.Join(", ", cleanedPids.Distinct().OrderBy(pid => pid)),
                reason);
        }
    }

    private async Task CleanupAnyAutomationToolProcessesAsync(string reason, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var powerShell = FindPowerShellExecutable();
        if (powerShell is null)
        {
            logger.LogWarning("Skipped AutomationTool cleanup because PowerShell was not found. Reason: {Reason}", reason);
            return;
        }

        var script = """
$targets = Get-CimInstance Win32_Process |
  Where-Object {
    $_.CommandLine -and
    ($_.CommandLine -match 'RunUAT\.bat' -or $_.CommandLine -match 'AutomationTool\.dll')
  } |
  Select-Object -ExpandProperty ProcessId

foreach ($targetPid in $targets) {
  try {
    taskkill /PID $targetPid /T /F | Out-Null
    Write-Output $targetPid
  }
  catch {
  }
}
""";

        var startInfo = new ProcessStartInfo(powerShell)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        using var registration = cancellationToken.Register(() => TryKillProcess(process));

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            logger.LogWarning(
                "AutomationTool cleanup exited with code {ExitCode}. Reason: {Reason}. Error: {Error}",
                process.ExitCode,
                reason,
                stderr.Trim());
            return;
        }

        var cleanedPids = stdout
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (cleanedPids.Length > 0)
        {
            logger.LogWarning(
                "Cleaned all stale AutomationTool process tree(s) {Pids}. Reason: {Reason}",
                string.Join(", ", cleanedPids),
                reason);
        }
    }

    private static bool ShouldTrack(ProcessCommand command)
    {
        return command.Arguments.Any(argument => argument.Contains("RunUAT.bat", StringComparison.OrdinalIgnoreCase)) ||
               command.DisplayString.Contains("RunUAT.bat", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeAutomationToolCommand(string? commandLine)
    {
        return !string.IsNullOrWhiteSpace(commandLine) &&
               (commandLine.Contains("RunUAT.bat", StringComparison.OrdinalIgnoreCase) ||
                commandLine.Contains("AutomationTool.dll", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<AutomationToolProcessTracker?> ReadTrackerAsync(string trackerPath, CancellationToken cancellationToken)
    {
        try
        {
            var json = await File.ReadAllTextAsync(trackerPath, cancellationToken);
            return JsonSerializer.Deserialize<AutomationToolProcessTracker>(json, TrackerJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> QueryCommandLineAsync(int processId, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var powerShell = FindPowerShellExecutable();
        if (powerShell is null)
        {
            return null;
        }

        var script = $$"""
$process = Get-CimInstance Win32_Process -Filter "ProcessId = {{processId}}"
if ($process -and $process.CommandLine) {
  Write-Output $process.CommandLine
}
""";

        var startInfo = new ProcessStartInfo(powerShell)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        using var registration = cancellationToken.Register(() => TryKillProcess(process));
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return output.Trim();
    }

    private static Process? TryGetProcess(int processId)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            return process.HasExited ? null : process;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> KillProcessTreeAsync(int processId, CancellationToken cancellationToken)
    {
        var taskKill = new ProcessStartInfo("taskkill")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        taskKill.ArgumentList.Add("/PID");
        taskKill.ArgumentList.Add(processId.ToString());
        taskKill.ArgumentList.Add("/T");
        taskKill.ArgumentList.Add("/F");

        using var process = new Process { StartInfo = taskKill };
        process.Start();
        using var registration = cancellationToken.Register(() => TryKillProcess(process));
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode == 0;
    }

    private static string? FindPowerShellExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            "powershell.exe",
            "pwsh.exe"
        };

        return candidates.FirstOrDefault(candidate =>
        {
            if (Path.IsPathRooted(candidate))
            {
                return File.Exists(candidate);
            }

            return true;
        });
    }

    private static void TryDeleteTracker(string trackerPath)
    {
        try
        {
            if (File.Exists(trackerPath))
            {
                File.Delete(trackerPath);
            }
        }
        catch
        {
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch
        {
        }
    }

    private static readonly JsonSerializerOptions TrackerJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private sealed record AutomationToolProcessTracker(
        Guid BuildId,
        int ProcessId,
        string ExecutableName,
        string CommandLine,
        DateTimeOffset TrackedAtUtc);
}
