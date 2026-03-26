namespace Backend.Options;

public sealed class AppOptions
{
    public const string SectionName = "App";

    public string ServerUrl { get; init; } = "http://0.0.0.0:5080";

    public string StorageRoot { get; init; } = "AppData";

    public int GlobalConcurrency { get; init; } = 2;

    public int UatConcurrency { get; init; } = 1;

    public bool AutomationToolCleanupEnabled { get; init; } = true;

    public string AutomationToolCleanupMode { get; init; } = "TrackedOnly";

    public int DefaultLogTailLines { get; init; } = 2000;

    public int MaxLiveLogLines { get; init; } = 4000;

    public int EventChannelCapacity { get; init; } = 256;

    public int EventHeartbeatSeconds { get; init; } = 15;

    public int LogEventBatchSize { get; init; } = 25;

    public int LogEventFlushMilliseconds { get; init; } = 250;

    public int BuildRetentionDays { get; init; } = 14;

    public int CleanupIntervalMinutes { get; init; } = 60;

    public string FrontendDevOrigin { get; init; } = "http://localhost:5173";
}
