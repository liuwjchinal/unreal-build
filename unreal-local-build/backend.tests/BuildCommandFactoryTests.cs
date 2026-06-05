using Backend.Models;
using Backend.Services;
using Xunit;

namespace Backend.Tests;

public sealed class BuildCommandFactoryTests
{
    [Fact]
    public void CreateUatCommand_AppendsUbaRemoteHostAndPort_WhenUbaEnabled()
    {
        var project = CreateProject();
        var build = CreateBuild(BuildAccelerator.Uba);
        build.UbaRemoteEnabled = true;
        build.UbaHost = "192.168.1.20";
        build.UbaListenHost = "0.0.0.0";
        build.UbaPort = 1345;
        build.UbaAgentStoreCapacityGb = 64;
        build.UbaAgentMaxIdleSeconds = 180;
        build.UbaMaxWorkers = 6;
        build.UbaAgentJoinUrl = "uba-agent://join?host=192.168.1.20&port=1345&buildId=test&maxIdle=120";

        var command = BuildCommandFactory.CreateUatCommand(project, build);

        var ubtArgs = Assert.Single(command.Arguments, arg => arg.StartsWith("-ubtargs=", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("-UBA", ubtArgs, StringComparison.Ordinal);
        Assert.Contains("-UBAPrintSummary", ubtArgs, StringComparison.Ordinal);
        Assert.Contains("-UBAHost=0.0.0.0", ubtArgs, StringComparison.Ordinal);
        Assert.Contains("-UBAPort=1345", ubtArgs, StringComparison.Ordinal);
        Assert.Contains("-UBAStoreCapacityGb=64", ubtArgs, StringComparison.Ordinal);
        Assert.Contains("-UBAMaxWorkers=6", ubtArgs, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateUatCommand_DoesNotAppendUbtArgs_WhenUbaDisabled()
    {
        var project = CreateProject();
        var build = CreateBuild(BuildAccelerator.None);

        var command = BuildCommandFactory.CreateUatCommand(project, build);

        Assert.DoesNotContain(command.Arguments, arg => arg.StartsWith("-ubtargs=", StringComparison.OrdinalIgnoreCase));
    }

    private static ProjectConfig CreateProject()
    {
        var root = Path.Combine(Path.GetTempPath(), "backend-uba-command-tests", Guid.NewGuid().ToString("N"));
        var engineRoot = Path.Combine(root, "EngineRoot");
        Directory.CreateDirectory(Path.Combine(engineRoot, "Engine", "Build", "BatchFiles"));

        return new ProjectConfig
        {
            Id = Guid.NewGuid(),
            ProjectKey = "test-project",
            ProjectFingerprint = "test-fingerprint",
            Name = "TestProject",
            WorkingCopyPath = root,
            UProjectPath = Path.Combine(root, "Test.uproject"),
            EngineRootPath = engineRoot,
            ArchiveRootPath = Path.Combine(root, "Archive"),
            GameTarget = "TestGame"
        };
    }

    private static BuildRecord CreateBuild(BuildAccelerator accelerator)
    {
        return new BuildRecord
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            Revision = "HEAD",
            Platform = BuildPlatform.Windows,
            TargetType = BuildTargetType.Game,
            TargetName = "TestGame",
            BuildConfiguration = "Development",
            BuildAccelerator = accelerator,
            ArchiveDirectoryPath = Path.Combine(Path.GetTempPath(), "backend-uba-command-tests", Guid.NewGuid().ToString("N"), "Archive")
        };
    }
}
