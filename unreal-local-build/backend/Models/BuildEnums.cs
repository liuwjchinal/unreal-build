namespace Backend.Models;

public enum BuildStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Interrupted
}

public enum BuildPhase
{
    Queued,
    SourceSync,
    Build,
    Cook,
    Stage,
    Package,
    Archive,
    Zip,
    Completed,
    Failed,
    Interrupted
}

public enum BuildStageLogKind
{
    SourceSync,
    Build,
    Cook,
    Stage,
    Package,
    Archive,
    Zip,
    UBT,
    Pak,
    IoStore
}

public enum BuildStageLogStatus
{
    Running,
    Completed,
    Failed,
    Interrupted
}

public enum BuildPlatform
{
    Windows,
    Android,
    OpenHarmony
}

public enum AndroidPackagingMode
{
    ExternalFilesIoStore,
    SplitObb,
    DataInsideApk
}

public enum BuildTargetType
{
    Game,
    Client,
    Server
}

public enum BuildTriggerSource
{
    Manual,
    Schedule
}

public enum BuildScheduleScopeType
{
    SingleProject,
    AllProjects
}

public enum BuildAccelerator
{
    None,
    Uba
}
