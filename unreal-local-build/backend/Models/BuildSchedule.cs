namespace Backend.Models;

public sealed class BuildSchedule
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public BuildScheduleScopeType ScopeType { get; set; } = BuildScheduleScopeType.SingleProject;

    public Guid? ProjectId { get; set; }

    public ProjectConfig? Project { get; set; }

    public string TimeOfDayLocal { get; set; } = "12:00";

    public BuildTargetType TargetType { get; set; } = BuildTargetType.Game;

    public string BuildConfiguration { get; set; } = "Development";

    public bool Clean { get; set; }

    public bool Pak { get; set; } = true;

    public bool IoStore { get; set; } = true;

    public List<string> ExtraUatArgs { get; set; } = new();

    public DateTimeOffset? LastTriggeredAtUtc { get; set; }

    public string? LastTriggeredLocalDate { get; set; }

    public int LastTriggeredBuildCount { get; set; }

    public string? LastTriggerMessage { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
