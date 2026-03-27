namespace Backend.Models;

public sealed class ProjectConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string ProjectKey { get; set; } = Guid.NewGuid().ToString("N");

    public string ProjectFingerprint { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string WorkingCopyPath { get; set; } = string.Empty;

    public string UProjectPath { get; set; } = string.Empty;

    public string EngineRootPath { get; set; } = string.Empty;

    public string ArchiveRootPath { get; set; } = string.Empty;

    public string? GameTarget { get; set; }

    public string? ClientTarget { get; set; }

    public string? ServerTarget { get; set; }

    public List<string> AllowedBuildConfigurations { get; set; } = new() { "Development", "Shipping" };

    public List<string> DefaultExtraUatArgs { get; set; } = new();

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public List<BuildSchedule> Schedules { get; set; } = new();

    public List<BuildRecord> Builds { get; set; } = new();
}
