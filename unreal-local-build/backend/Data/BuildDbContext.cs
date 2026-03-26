using System.Text.Json;
using Backend.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Backend.Data;

public sealed class BuildDbContext(DbContextOptions<BuildDbContext> options) : DbContext(options)
{
    public DbSet<ProjectConfig> Projects => Set<ProjectConfig>();

    public DbSet<BuildRecord> Builds => Set<BuildRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var stringListConverter = new ValueConverter<List<string>, string>(
            value => JsonSerializer.Serialize(value, JsonSerializerOptions.Default),
            value => string.IsNullOrWhiteSpace(value)
                ? new List<string>()
                : JsonSerializer.Deserialize<List<string>>(value, JsonSerializerOptions.Default) ?? new List<string>());

        var stringListComparer = new ValueComparer<List<string>>(
            (left, right) => (left ?? new List<string>()).SequenceEqual(right ?? new List<string>()),
            value => value.Aggregate(0, (current, item) => HashCode.Combine(current, StringComparer.OrdinalIgnoreCase.GetHashCode(item))),
            value => value.ToList());

        modelBuilder.Entity<ProjectConfig>(entity =>
        {
            entity.ToTable("Projects");
            entity.HasKey(project => project.Id);
            entity.Property(project => project.ProjectKey).HasMaxLength(64);
            entity.Property(project => project.ProjectFingerprint).HasMaxLength(3000);
            entity.Property(project => project.Name).HasMaxLength(200);
            entity.Property(project => project.WorkingCopyPath).HasMaxLength(1000);
            entity.Property(project => project.UProjectPath).HasMaxLength(1000);
            entity.Property(project => project.EngineRootPath).HasMaxLength(1000);
            entity.Property(project => project.ArchiveRootPath).HasMaxLength(1000);
            entity.Property(project => project.GameTarget).HasMaxLength(200);
            entity.Property(project => project.ClientTarget).HasMaxLength(200);
            entity.Property(project => project.ServerTarget).HasMaxLength(200);
            entity.Property(project => project.AllowedBuildConfigurations)
                .HasConversion(stringListConverter)
                .Metadata.SetValueComparer(stringListComparer);
            entity.Property(project => project.DefaultExtraUatArgs)
                .HasConversion(stringListConverter)
                .Metadata.SetValueComparer(stringListComparer);
            entity.HasIndex(project => project.ProjectKey).IsUnique();
            entity.HasIndex(project => project.ProjectFingerprint);
        });

        modelBuilder.Entity<BuildRecord>(entity =>
        {
            entity.ToTable("Builds");
            entity.HasKey(build => build.Id);
            entity.Property(build => build.Status).HasConversion<string>();
            entity.Property(build => build.CurrentPhase).HasConversion<string>();
            entity.Property(build => build.TargetType).HasConversion<string>();
            entity.Property(build => build.LogFilePath).HasMaxLength(1000);
            entity.Property(build => build.BuildRootPath).HasMaxLength(1000);
            entity.Property(build => build.ArchiveDirectoryPath).HasMaxLength(1000);
            entity.Property(build => build.ZipFilePath).HasMaxLength(1000);
            entity.Property(build => build.DownloadUrl).HasMaxLength(500);
            entity.Property(build => build.TargetName).HasMaxLength(200);
            entity.Property(build => build.BuildConfiguration).HasMaxLength(100);
            entity.Property(build => build.Revision).HasMaxLength(100);
            entity.Property(build => build.StatusMessage).HasMaxLength(500);
            entity.Property(build => build.ErrorSummary).HasMaxLength(4000);
            entity.Property(build => build.SvnCommandLine).HasMaxLength(4000);
            entity.Property(build => build.UatCommandLine).HasMaxLength(4000);
            entity.Property(build => build.ExtraUatArgs)
                .HasConversion(stringListConverter)
                .Metadata.SetValueComparer(stringListComparer);
            entity.HasOne(build => build.Project)
                .WithMany(project => project.Builds)
                .HasForeignKey(build => build.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(build => new { build.ProjectId, build.QueuedAtUtc });
            entity.HasIndex(build => build.Status);
            entity.HasIndex(build => build.FinishedAtUtc);
        });
    }
}
