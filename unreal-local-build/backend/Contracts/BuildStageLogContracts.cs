using Backend.Models;

namespace Backend.Contracts;

public sealed record BuildStageArtifactDto(
    string ArtifactKey,
    string Label,
    string Category,
    string FileName,
    long SizeBytes,
    string DownloadUrl);

public sealed record BuildStageLogSummaryDto(
    string StageKey,
    BuildStageLogKind Kind,
    string DisplayName,
    string? ParentStageKey,
    int Order,
    BuildStageLogStatus Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    long LogLineCount,
    string LogDownloadUrl,
    IReadOnlyList<BuildStageArtifactDto> InputArtifacts);

public sealed record BuildStageLogListDto(IReadOnlyList<BuildStageLogSummaryDto> Stages);

public sealed record BuildStageLogSnapshotDto(
    BuildStageLogSummaryDto Stage,
    IReadOnlyList<string> Lines,
    int IncludedLines,
    long TotalLines,
    bool Truncated);
