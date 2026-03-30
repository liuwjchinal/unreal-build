using Backend.Models;
using Backend.Services;

namespace Backend.Contracts;

public sealed record UpsertProjectRequest(
    string? ProjectKey,
    string Name,
    string WorkingCopyPath,
    string UProjectPath,
    string EngineRootPath,
    string ArchiveRootPath,
    string? GameTarget,
    string? ClientTarget,
    string? ServerTarget,
    bool? AndroidEnabled,
    string? AndroidTextureFlavor,
    List<string>? AllowedBuildConfigurations,
    List<string>? DefaultExtraUatArgs);

public sealed record ProjectSummaryDto(
    Guid Id,
    string ProjectKey,
    string Name,
    string WorkingCopyDisplayPath,
    string UProjectDisplayPath,
    string EngineDisplayPath,
    string ArchiveDisplayPath,
    string? GameTarget,
    string? ClientTarget,
    string? ServerTarget,
    bool AndroidEnabled,
    string AndroidTextureFlavor,
    IReadOnlyList<string> AllowedBuildConfigurations,
    IReadOnlyList<string> DefaultExtraUatArgs,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record ProjectConfigDto(
    Guid Id,
    string ProjectKey,
    string Name,
    string WorkingCopyPath,
    string UProjectPath,
    string EngineRootPath,
    string ArchiveRootPath,
    string? GameTarget,
    string? ClientTarget,
    string? ServerTarget,
    bool AndroidEnabled,
    string AndroidTextureFlavor,
    IReadOnlyList<string> AllowedBuildConfigurations,
    IReadOnlyList<string> DefaultExtraUatArgs,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record ImportProjectConflictDto(
    string Name,
    string? ProjectKey,
    string Reason);

public sealed record ImportProjectsResultDto(
    int Created,
    int Updated,
    int Conflicts,
    int Total,
    IReadOnlyList<ImportProjectConflictDto> ConflictItems);

public static class ProjectContractMappings
{
    public static ProjectConfig ToEntity(this UpsertProjectRequest request)
    {
        var project = new ProjectConfig();
        request.Apply(project);
        project.CreatedAtUtc = DateTimeOffset.UtcNow;
        project.UpdatedAtUtc = DateTimeOffset.UtcNow;
        return project;
    }

    public static void Apply(this UpsertProjectRequest request, ProjectConfig project)
    {
        project.ProjectKey = ProjectIdentity.EnsureProjectKey(request.ProjectKey ?? project.ProjectKey);
        project.Name = request.Name.Trim();
        project.WorkingCopyPath = request.WorkingCopyPath.Trim();
        project.UProjectPath = request.UProjectPath.Trim();
        project.EngineRootPath = request.EngineRootPath.Trim();
        project.ArchiveRootPath = request.ArchiveRootPath.Trim();
        project.GameTarget = request.GameTarget?.Trim();
        project.ClientTarget = request.ClientTarget?.Trim();
        project.ServerTarget = request.ServerTarget?.Trim();
        project.AndroidEnabled = NormalizeAndroidEnabled(request.AndroidEnabled);
        project.AndroidTextureFlavor = NormalizeAndroidTextureFlavor(request.AndroidTextureFlavor);
        project.AllowedBuildConfigurations = NormalizeList(request.AllowedBuildConfigurations, new[] { "Development", "Shipping" });
        project.DefaultExtraUatArgs = NormalizeList(request.DefaultExtraUatArgs, Array.Empty<string>());
        project.ProjectFingerprint = ProjectIdentity.CreateFingerprint(
            project.WorkingCopyPath,
            project.UProjectPath,
            project.EngineRootPath);
        project.UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    public static ProjectSummaryDto ToSummaryDto(this ProjectConfig project)
    {
        return new ProjectSummaryDto(
            project.Id,
            project.ProjectKey,
            project.Name,
            PathDisplay.Mask(project.WorkingCopyPath),
            PathDisplay.Mask(project.UProjectPath),
            PathDisplay.Mask(project.EngineRootPath),
            PathDisplay.Mask(project.ArchiveRootPath),
            project.GameTarget,
            project.ClientTarget,
            project.ServerTarget,
            project.AndroidEnabled,
            project.AndroidTextureFlavor,
            project.AllowedBuildConfigurations,
            project.DefaultExtraUatArgs,
            project.CreatedAtUtc,
            project.UpdatedAtUtc);
    }

    public static ProjectConfigDto ToConfigDto(this ProjectConfig project)
    {
        return new ProjectConfigDto(
            project.Id,
            project.ProjectKey,
            project.Name,
            project.WorkingCopyPath,
            project.UProjectPath,
            project.EngineRootPath,
            project.ArchiveRootPath,
            project.GameTarget,
            project.ClientTarget,
            project.ServerTarget,
            project.AndroidEnabled,
            project.AndroidTextureFlavor,
            project.AllowedBuildConfigurations,
            project.DefaultExtraUatArgs,
            project.CreatedAtUtc,
            project.UpdatedAtUtc);
    }

    private static string NormalizeAndroidTextureFlavor(string? value)
    {
        var flavor = value?.Trim();
        return string.IsNullOrWhiteSpace(flavor) ? "ASTC" : flavor.ToUpperInvariant();
    }

    private static bool NormalizeAndroidEnabled(bool? value)
    {
        return value ?? true;
    }

    private static List<string> NormalizeList(IEnumerable<string>? values, IEnumerable<string> fallback)
    {
        var normalized = (values ?? fallback)
            .Select(item => item.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return normalized.Count == 0 ? fallback.ToList() : normalized;
    }
}
