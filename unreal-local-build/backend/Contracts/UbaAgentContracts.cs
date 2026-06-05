namespace Backend.Contracts;

public sealed record UbaAgentConfigDto(
    bool Enabled,
    string Host,
    int Port,
    int MaxIdleSeconds,
    int StoreCapacityGb,
    int MaxWorkers,
    string PackageDownloadUrl,
    string ProtocolExampleUrl,
    string ManualCommandExample,
    bool HostAutoDetected,
    string? HostWarning,
    int PortPoolSize);
