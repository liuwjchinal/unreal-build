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
