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

    [Fact]
    public void CreateUatCommand_AppendsAndroidExternalFilesPackagingOverrides_WhenPlatformIsAndroid()
    {
        var project = CreateProject();
        var build = CreateBuild(BuildAccelerator.None);
        build.Platform = BuildPlatform.Android;
        build.Pak = false;
        build.IoStore = false;

        var command = BuildCommandFactory.CreateUatCommand(project, build);

        Assert.Contains("-targetplatform=Android", command.Arguments);
        Assert.Contains("-pak", command.Arguments);
        Assert.Contains("-iostore", command.Arguments);
        Assert.DoesNotContain("-skipiostore", command.Arguments);
        Assert.Contains("-cookflavor=ASTC", command.Arguments);
        Assert.Contains("-manifests", command.Arguments);
        Assert.Contains("-ini:Game:[/Script/UnrealEd.ProjectPackagingSettings]:bGenerateChunks=True", command.Arguments);
        Assert.Contains("-ini:Game:[/Script/UnrealEd.ProjectPackagingSettings]:bGenerateNoChunks=False", command.Arguments);
        Assert.Contains("-ini:Game:[/Script/UnrealEd.ProjectPackagingSettings]:MaxChunkSize=900000000", command.Arguments);
        Assert.Contains("-ini:Game:[/Script/UnrealEd.ProjectPackagingSettings]:MaxIoStorePartitionSizeMB=900", command.Arguments);
        Assert.Contains("-ini:Engine:[/Script/AndroidRuntimeSettings.AndroidRuntimeSettings]:bPackageDataInsideApk=False", command.Arguments);
        Assert.Contains("-ini:Engine:[/Script/AndroidRuntimeSettings.AndroidRuntimeSettings]:bUseExternalFilesDir=True", command.Arguments);
        Assert.Contains("-ini:Engine:[/Script/AndroidRuntimeSettings.AndroidRuntimeSettings]:+ObbFilters=-.../Content/Paks/...", command.Arguments);
        Assert.DoesNotContain("-ini:Engine:[/Script/AndroidRuntimeSettings.AndroidRuntimeSettings]:bForceSmallOBBFiles=True", command.Arguments);
        Assert.DoesNotContain("-ini:Engine:[/Script/AndroidRuntimeSettings.AndroidRuntimeSettings]:bAllowOverflowOBBFiles=True", command.Arguments);
        Assert.NotNull(command.EnvironmentVariables);
    }

    [Fact]
    public void CreateUatCommand_AppendsAndroidSplitObbPackagingOverrides_WhenRequested()
    {
        var project = CreateProject();
        var build = CreateBuild(BuildAccelerator.None);
        build.Platform = BuildPlatform.Android;
        build.AndroidPackagingMode = AndroidPackagingMode.SplitObb;

        var command = BuildCommandFactory.CreateUatCommand(project, build);

        Assert.Contains("-ini:Engine:[/Script/AndroidRuntimeSettings.AndroidRuntimeSettings]:bPackageDataInsideApk=False", command.Arguments);
        Assert.Contains("-ini:Engine:[/Script/AndroidRuntimeSettings.AndroidRuntimeSettings]:bUseExternalFilesDir=False", command.Arguments);
        Assert.Contains("-ini:Engine:[/Script/AndroidRuntimeSettings.AndroidRuntimeSettings]:bForceSmallOBBFiles=True", command.Arguments);
        Assert.Contains("-ini:Engine:[/Script/AndroidRuntimeSettings.AndroidRuntimeSettings]:bAllowLargeOBBFiles=False", command.Arguments);
        Assert.Contains("-ini:Engine:[/Script/AndroidRuntimeSettings.AndroidRuntimeSettings]:bAllowPatchOBBFile=True", command.Arguments);
        Assert.Contains("-ini:Engine:[/Script/AndroidRuntimeSettings.AndroidRuntimeSettings]:bAllowOverflowOBBFiles=True", command.Arguments);
        Assert.Contains("-ini:Engine:[/Script/AndroidRuntimeSettings.AndroidRuntimeSettings]:OverflowOBBFileLimit=16", command.Arguments);
    }

    [Fact]
    public void CreateUatCommand_AppendsAndroidDataInsideApkPackagingOverrides_WhenRequested()
    {
        var project = CreateProject();
        var build = CreateBuild(BuildAccelerator.None);
        build.Platform = BuildPlatform.Android;
        build.AndroidPackagingMode = AndroidPackagingMode.DataInsideApk;

        var command = BuildCommandFactory.CreateUatCommand(project, build);

        Assert.Contains("-ini:Engine:[/Script/AndroidRuntimeSettings.AndroidRuntimeSettings]:bPackageDataInsideApk=True", command.Arguments);
        Assert.Contains("-ini:Engine:[/Script/AndroidRuntimeSettings.AndroidRuntimeSettings]:bUseExternalFilesDir=False", command.Arguments);
        Assert.DoesNotContain("-ini:Engine:[/Script/AndroidRuntimeSettings.AndroidRuntimeSettings]:bForceSmallOBBFiles=True", command.Arguments);
    }

    [Fact]
    public void CreateUatCommand_DoesNotAppendAndroidPackagingOverrides_WhenPlatformIsWindows()
    {
        var project = CreateProject();
        var build = CreateBuild(BuildAccelerator.None);

        var command = BuildCommandFactory.CreateUatCommand(project, build);

        Assert.DoesNotContain(command.Arguments, arg => arg.StartsWith("-ini:Game:", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(command.Arguments, arg => arg.StartsWith("-ini:Engine:", StringComparison.OrdinalIgnoreCase));
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
