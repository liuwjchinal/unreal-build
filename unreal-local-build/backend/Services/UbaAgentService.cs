using System.IO.Compression;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Backend.Contracts;
using Backend.Models;
using Backend.Options;

namespace Backend.Services;

public sealed class UbaPortUnavailableException(string message) : Exception(message);

public sealed class UbaAgentService(AppOptions appOptions)
{
    private const string PackageUrl = "/api/uba-agent/package";
    private const string DefaultProtocolScheme = "uba-agent";
    private const int DefaultPort = 1345;
    private const int DefaultStoreCapacityGb = 40;
    private const int DefaultMaxIdleSeconds = 120;
    private const int DefaultMaxWorkers = 4;

    private readonly object _portSync = new();
    private readonly ConcurrentDictionary<Guid, int> _reservedPorts = new();
    private readonly object _packageCacheSync = new();
    private PackageCacheEntry? _packageCache;

    public bool IsEnabled => appOptions.UbaRemoteAgentEnabled;

    public int Port => NormalizePort(appOptions.UbaPort);

    public int PortPoolSize => Math.Clamp(
        appOptions.UbaPortPoolSize <= 0 ? 1 : appOptions.UbaPortPoolSize,
        1,
        65535 - Port + 1);

    public int MaxIdleSeconds => Math.Max(30, appOptions.UbaAgentMaxIdleSeconds <= 0 ? DefaultMaxIdleSeconds : appOptions.UbaAgentMaxIdleSeconds);

    public int StoreCapacityGb => Math.Max(1, appOptions.UbaAgentStoreCapacityGb <= 0 ? DefaultStoreCapacityGb : appOptions.UbaAgentStoreCapacityGb);

    public int MaxWorkers => Math.Max(1, appOptions.UbaMaxWorkers <= 0 ? DefaultMaxWorkers : appOptions.UbaMaxWorkers);

    private string ProtocolScheme => string.IsNullOrWhiteSpace(appOptions.UbaJoinScheme)
        ? DefaultProtocolScheme
        : appOptions.UbaJoinScheme.Trim();

    public string ResolveBuildHost() => ResolvePublicHostSnapshot().Host;

    public UbaAgentConfigDto CreateConfigDto()
    {
        var host = ResolvePublicHostSnapshot();
        var protocolUrl = CreateJoinUrl(Guid.Empty, host.Host, Port, MaxIdleSeconds);
        return new UbaAgentConfigDto(
            IsEnabled,
            host.Host,
            Port,
            MaxIdleSeconds,
            StoreCapacityGb,
            MaxWorkers,
            PackageUrl,
            protocolUrl,
            CreateManualCommand(host.Host, Port, StoreCapacityGb, MaxIdleSeconds),
            host.AutoDetected,
            host.Warning,
            PortPoolSize);
    }

    public void ApplyUbaSnapshot(BuildRecord build)
    {
        if (!IsRemoteEnabledFor(build.BuildAccelerator))
        {
            build.UbaRemoteEnabled = false;
            build.UbaHost = null;
            build.UbaListenHost = null;
            build.UbaPort = null;
            build.UbaAgentMaxIdleSeconds = null;
            build.UbaAgentStoreCapacityGb = null;
            build.UbaMaxWorkers = null;
            build.UbaAgentJoinUrl = null;
            build.UbaAgentManualCommand = null;
            build.UbaHostAutoDetected = false;
            build.UbaHostWarning = null;
            return;
        }

        var host = ResolvePublicHostSnapshot();
        var port = ReservePortForBuild(build.Id, build.UbaPort);
        var maxIdleSeconds = MaxIdleSeconds;
        var storeCapacityGb = StoreCapacityGb;
        var maxWorkers = MaxWorkers;
        build.UbaRemoteEnabled = true;
        build.UbaHost = host.Host;
        build.UbaListenHost = ResolveListenHost();
        build.UbaPort = port;
        build.UbaAgentMaxIdleSeconds = maxIdleSeconds;
        build.UbaAgentStoreCapacityGb = storeCapacityGb;
        build.UbaMaxWorkers = maxWorkers;
        build.UbaAgentJoinUrl = CreateJoinUrl(build.Id, host.Host, port, maxIdleSeconds);
        build.UbaAgentManualCommand = CreateManualCommand(host.Host, port, storeCapacityGb, maxIdleSeconds);
        build.UbaHostAutoDetected = host.AutoDetected;
        build.UbaHostWarning = host.Warning;
    }

    public IReadOnlyList<string> BuildUbtArgs(BuildAccelerator accelerator)
    {
        if (!IsRemoteEnabledFor(accelerator))
        {
            return Array.Empty<string>();
        }

        return
        [
            "-UBA",
            "-UBAPrintSummary",
            $"-UBAHost={ResolveListenHost()}",
            $"-UBAPort={Port}",
            $"-UBAStoreCapacityGb={StoreCapacityGb}",
            $"-UBAMaxWorkers={MaxWorkers}"
        ];
    }

    public bool IsRemoteEnabledFor(BuildAccelerator accelerator)
    {
        return IsEnabled && accelerator == BuildAccelerator.Uba;
    }

    public string CreateManualCommand(string host, int port)
    {
        return CreateManualCommand(host, port, StoreCapacityGb, MaxIdleSeconds);
    }

    public string CreateManualCommand(string host, int port, int storeCapacityGb, int maxIdleSeconds)
    {
        var cachePath = @"%LOCALAPPDATA%\UnrealLocalBuild\UbaAgent\Cache";
        return $"UbaAgent.exe -host={host}:{port} -dir=\"{cachePath}\" -capacity={Math.Max(1, storeCapacityGb)} -maxidle={Math.Max(30, maxIdleSeconds)} -summary -quiet";
    }

    public void ReleasePortForBuild(Guid buildId)
    {
        _reservedPorts.TryRemove(buildId, out _);
    }

    public void RebuildPortReservations(IEnumerable<BuildRecord> builds)
    {
        lock (_portSync)
        {
            _reservedPorts.Clear();
            foreach (var build in builds)
            {
                if (build.UbaRemoteEnabled && build.UbaPort.HasValue && IsPortInPool(build.UbaPort.Value))
                {
                    _reservedPorts.TryAdd(build.Id, build.UbaPort.Value);
                }
            }
        }
    }

    public string? ResolveAgentPackageSource(ProjectConfig? project = null)
    {
        if (!string.IsNullOrWhiteSpace(appOptions.UbaAgentPackageSourcePath))
        {
            return Path.GetFullPath(appOptions.UbaAgentPackageSourcePath);
        }

        if (project is null || string.IsNullOrWhiteSpace(project.EngineRootPath))
        {
            return null;
        }

        return Path.Combine(project.EngineRootPath, "Engine", "Binaries", "Win64", "UnrealBuildAccelerator", "x64");
    }

    public async Task<(byte[] Bytes, string FileName)> CreateAgentPackageAsync(ProjectConfig? project, CancellationToken cancellationToken)
    {
        var sourcePath = ResolveAgentPackageSource(project);
        if (string.IsNullOrWhiteSpace(sourcePath) || !Directory.Exists(sourcePath))
        {
            throw new DirectoryNotFoundException("UBA Agent package source directory was not found.");
        }

        var files = EnumerateAgentPackageFiles(sourcePath).ToList();
        if (!files.Any(file => Path.GetFileName(file).Equals("UbaAgent.exe", StringComparison.OrdinalIgnoreCase)))
        {
            throw new FileNotFoundException("UBA Agent package source does not contain UbaAgent.exe.");
        }

        var fingerprint = CreatePackageFingerprint(sourcePath, files);
        lock (_packageCacheSync)
        {
            if (_packageCache is not null &&
                string.Equals(_packageCache.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_packageCache.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return (_packageCache.Bytes, _packageCache.FileName);
            }
        }

        await using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var filePath in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = archive.CreateEntry(Path.GetFileName(filePath), CompressionLevel.Fastest);
                await using var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                await using var output = entry.Open();
                await input.CopyToAsync(output, cancellationToken);
            }

            AddTextEntry(archive, "install-uba-agent-protocol.ps1", BuildInstallScript());
            AddTextEntry(archive, "README.txt", BuildReadme());
        }

        var package = (Bytes: stream.ToArray(), FileName: "uba-agent-win64.zip");
        lock (_packageCacheSync)
        {
            _packageCache = new PackageCacheEntry(sourcePath, fingerprint, package.Bytes, package.FileName);
        }

        return package;
    }

    private int ReservePortForBuild(Guid buildId, int? preferredPort)
    {
        lock (_portSync)
        {
            if (_reservedPorts.TryGetValue(buildId, out var existingPort))
            {
                return existingPort;
            }

            var usedPorts = _reservedPorts.Values.ToHashSet();
            if (preferredPort.HasValue && IsPortInPool(preferredPort.Value) && !usedPorts.Contains(preferredPort.Value))
            {
                _reservedPorts[buildId] = preferredPort.Value;
                return preferredPort.Value;
            }

            for (var offset = 0; offset < PortPoolSize; offset++)
            {
                var candidate = Port + offset;
                if (!usedPorts.Contains(candidate))
                {
                    _reservedPorts[buildId] = candidate;
                    return candidate;
                }
            }
        }

        throw new UbaPortUnavailableException($"No UBA port is available. Increase App:UbaPortPoolSize or reduce concurrent queued/running UBA builds.");
    }

    private bool IsPortInPool(int port)
    {
        return port >= Port && port < Port + PortPoolSize;
    }

    private string CreateJoinUrl(Guid buildId, string host, int port, int maxIdleSeconds)
    {
        var query = new Dictionary<string, string>
        {
            ["host"] = host,
            ["port"] = port.ToString(),
            ["buildId"] = buildId == Guid.Empty ? "00000000-0000-0000-0000-000000000000" : buildId.ToString(),
            ["maxIdle"] = maxIdleSeconds.ToString()
        };

        return $"{ProtocolScheme}://join?{string.Join('&', query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"))}";
    }

    private HostResolution ResolvePublicHostSnapshot()
    {
        if (!string.IsNullOrWhiteSpace(appOptions.UbaPublicHost))
        {
            return new HostResolution(appOptions.UbaPublicHost.Trim(), false, null);
        }

        var candidate = NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.OperationalStatus == OperationalStatus.Up)
            .Where(item => item.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .SelectMany(item => item.GetIPProperties().UnicastAddresses)
            .Select(item => item.Address)
            .Where(item => item.AddressFamily == AddressFamily.InterNetwork)
            .Where(item => !IPAddress.IsLoopback(item))
            .Select(item => item.ToString())
            .FirstOrDefault(IsPrivateIpv4Address);

        var host = candidate ?? Dns.GetHostName();
        return new HostResolution(
            host,
            true,
            $"App:UbaPublicHost is not configured. Auto-detected {host}; if remote agents cannot connect, set App:UbaPublicHost to the build machine LAN IP.");
    }

    private string ResolveListenHost()
    {
        return string.IsNullOrWhiteSpace(appOptions.UbaRemoteHost)
            ? "0.0.0.0"
            : appOptions.UbaRemoteHost.Trim();
    }

    private static int NormalizePort(int port)
    {
        return port is > 0 and <= 65535 ? port : DefaultPort;
    }

    private static bool IsPrivateIpv4Address(string value)
    {
        if (value.StartsWith("10.", StringComparison.Ordinal) ||
            value.StartsWith("192.168.", StringComparison.Ordinal))
        {
            return true;
        }

        if (!value.StartsWith("172.", StringComparison.Ordinal) ||
            !IPAddress.TryParse(value, out var address))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[1] is >= 16 and <= 31;
    }

    private static IEnumerable<string> EnumerateAgentPackageFiles(string sourcePath)
    {
        return Directory.EnumerateFiles(sourcePath, "*", SearchOption.TopDirectoryOnly)
            .Where(ShouldIncludeAgentFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static string CreatePackageFingerprint(string sourcePath, IReadOnlyList<string> files)
    {
        return string.Join(
            "|",
            new[] { Path.GetFullPath(sourcePath) }.Concat(files.Select(file =>
            {
                var info = new FileInfo(file);
                return $"{Path.GetFileName(file)}:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
            })));
    }

    private static bool ShouldIncludeAgentFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals("UbaAgent.exe", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("UbaAgent.toml", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("UbaDetours.dll", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("UbaHost.dll", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("sentry.dll", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("crashpad_handler.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddTextEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new System.Text.UTF8Encoding(false));
        writer.Write(content);
    }

    private string BuildReadme()
    {
        return $"""
        Unreal Local Build - UBA Agent

        1. Extract this zip to a local folder.
        2. Right-click install-uba-agent-protocol.ps1 and run it with PowerShell.
        3. Open the build detail page and click "Accelerate Build".

        Manual command example:
        {CreateManualCommand(ResolveBuildHost(), Port)}
        """;
    }

    private string BuildInstallScript()
    {
        var scheme = ProtocolScheme;
        return $$"""
        param(
            [string]$InstallDir = $PSScriptRoot
        )

        $ErrorActionPreference = 'Stop'
        $agentPath = Join-Path $InstallDir 'UbaAgent.exe'
        if (-not (Test-Path -LiteralPath $agentPath)) {
            throw "UbaAgent.exe not found in $InstallDir"
        }

        $launcherPath = Join-Path $InstallDir 'Start-UbaAgentFromProtocol.ps1'
        $launcher = @'
        param([string]$Uri)
        $ErrorActionPreference = 'Stop'

        $localAppData = if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) { $env:TEMP } else { $env:LOCALAPPDATA }
        $logRoot = Join-Path $localAppData 'UnrealLocalBuild\UbaAgent\Logs'
        New-Item -ItemType Directory -Force -Path $logRoot | Out-Null
        $logPath = Join-Path $logRoot 'uba-agent-protocol.log'

        function Write-UbaAgentLog([string]$Message) {
            $line = '[{0:yyyy-MM-dd HH:mm:ss}] {1}' -f (Get-Date), $Message
            Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
        }

        try {
            Write-UbaAgentLog "Received URI: $Uri"
            if ([string]::IsNullOrWhiteSpace($Uri)) { throw 'Missing protocol URL.' }

            Add-Type -AssemblyName System.Web
            $parsed = [Uri]$Uri
            $query = [System.Web.HttpUtility]::ParseQueryString($parsed.Query)
            $hostName = $query['host']
            $port = $query['port']
            $maxIdle = $query['maxIdle']
            if ([string]::IsNullOrWhiteSpace($hostName)) { throw 'Missing host in {{scheme}} URL.' }
            if ([string]::IsNullOrWhiteSpace($port)) { $port = '{{Port}}' }
            if ([string]::IsNullOrWhiteSpace($maxIdle)) { $maxIdle = '{{MaxIdleSeconds}}' }
            Write-UbaAgentLog "Resolved host=$hostName port=$port maxIdle=$maxIdle"

            $installDir = Split-Path -Parent $MyInvocation.MyCommand.Path
            $agent = Join-Path $installDir 'UbaAgent.exe'
            if (-not (Test-Path -LiteralPath $agent)) { throw "UbaAgent.exe not found at $agent" }

            $cache = Join-Path $localAppData 'UnrealLocalBuild\UbaAgent\Cache'
            New-Item -ItemType Directory -Force -Path $cache | Out-Null

            $existing = Get-CimInstance Win32_Process |
                Where-Object {
                    $_.Name -ieq 'UbaAgent.exe' -and
                    $_.CommandLine -like "*-host=$hostName`:$port*"
                } |
                Select-Object -First 1

            if ($existing) {
                Write-UbaAgentLog "Existing UbaAgent.exe process $($existing.ProcessId) already handles $hostName`:$port"
                exit 0
            }

            $args = @(
                "-host=$hostName`:$port",
                "-dir=$cache",
                '-capacity={{StoreCapacityGb}}',
                "-maxidle=$maxIdle",
                '-summary',
                '-quiet'
            )

            Write-UbaAgentLog "Starting $agent $($args -join ' ')"
            Start-Process -FilePath $agent -ArgumentList $args -WindowStyle Hidden
            Write-UbaAgentLog 'Started UbaAgent.exe'
        }
        catch {
            Write-UbaAgentLog "Error: $($_.Exception.Message)"
            throw
        }
        '@
        Set-Content -LiteralPath $launcherPath -Value $launcher -Encoding UTF8

        $command = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$launcherPath`" `"%1`""
        New-Item -Path 'HKCU:\Software\Classes\{{scheme}}' -Force | Out-Null
        Set-ItemProperty -Path 'HKCU:\Software\Classes\{{scheme}}' -Name '(default)' -Value 'URL:UBA Agent Protocol'
        New-ItemProperty -Path 'HKCU:\Software\Classes\{{scheme}}' -Name 'URL Protocol' -Value '' -Force | Out-Null
        New-Item -Path 'HKCU:\Software\Classes\{{scheme}}\shell\open\command' -Force | Out-Null
        Set-ItemProperty -Path 'HKCU:\Software\Classes\{{scheme}}\shell\open\command' -Name '(default)' -Value $command

        Write-Host "{{scheme}}:// protocol registered for $agentPath"
        """;
    }

    private sealed record HostResolution(string Host, bool AutoDetected, string? Warning);

    private sealed record PackageCacheEntry(string SourcePath, string Fingerprint, byte[] Bytes, string FileName);
}
