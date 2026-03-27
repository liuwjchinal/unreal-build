namespace Backend.Services;

public sealed class BuildScheduleRuntimeState
{
    public DateTimeOffset? LastScheduleTickUtc { get; set; }

    public int EnabledScheduleCount { get; set; }
}
