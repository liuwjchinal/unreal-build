using Backend.Services;

namespace Backend.Models;

public sealed class BuildRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }

    public ProjectConfig? Project { get; set; }

    public string Revision { get; set; } = "HEAD";

    public BuildTriggerSource TriggerSource { get; set; } = BuildTriggerSource.Manual;

    public Guid? ScheduleId { get; set; }

    public BuildTargetType TargetType { get; set; }

    public string TargetName { get; set; } = string.Empty;

    public string BuildConfiguration { get; set; } = "Development";

    public bool Clean { get; set; }

    public bool Pak { get; set; } = true;

    public bool IoStore { get; set; } = true;

    public List<string> ExtraUatArgs { get; set; } = new();

    public BuildStatus Status { get; set; } = BuildStatus.Queued;

    public BuildPhase CurrentPhase { get; set; } = BuildPhase.Queued;

    public int ProgressPercent { get; set; }

    public string StatusMessage { get; set; } = AppText.WaitingToRun;

    public DateTimeOffset QueuedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? StartedAtUtc { get; set; }

    public DateTimeOffset? FinishedAtUtc { get; set; }

    public string LogFilePath { get; set; } = string.Empty;

    public string BuildRootPath { get; set; } = string.Empty;

    public string ArchiveDirectoryPath { get; set; } = string.Empty;

    public string? ZipFilePath { get; set; }

    public string? DownloadUrl { get; set; }

    public int? ExitCode { get; set; }

    public string? ErrorSummary { get; set; }

    public long LogLineCount { get; set; }

    public string? SvnCommandLine { get; set; }

    public string? UatCommandLine { get; set; }

    public string DisplayRevision => StoragePaths.FormatSvnRevision(Revision);
}
