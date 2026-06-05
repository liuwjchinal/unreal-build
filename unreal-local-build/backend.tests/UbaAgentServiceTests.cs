using System.IO.Compression;
using Backend.Models;
using Backend.Options;
using Backend.Services;
using Xunit;

namespace Backend.Tests;

public sealed class UbaAgentServiceTests
{
    [Fact]
    public void CreateConfigDto_ReturnsConfiguredRemoteAgentSettings()
    {
        var service = new UbaAgentService(new AppOptions
        {
            UbaRemoteAgentEnabled = true,
            UbaPublicHost = "192.168.10.20",
            UbaPort = 1345,
            UbaAgentMaxIdleSeconds = 120,
            UbaAgentStoreCapacityGb = 40,
            UbaMaxWorkers = 6
        });

        var config = service.CreateConfigDto();

        Assert.True(config.Enabled);
        Assert.Equal("192.168.10.20", config.Host);
        Assert.Equal(1345, config.Port);
        Assert.Equal(6, config.MaxWorkers);
        Assert.Equal(16, config.PortPoolSize);
        Assert.Equal("/api/uba-agent/package", config.PackageDownloadUrl);
        Assert.Contains("uba-agent://join?", config.ProtocolExampleUrl, StringComparison.Ordinal);
        Assert.Contains("host=192.168.10.20", config.ProtocolExampleUrl, StringComparison.Ordinal);
        Assert.Contains("port=1345", config.ProtocolExampleUrl, StringComparison.Ordinal);
        Assert.Contains("UbaAgent.exe -host=192.168.10.20:1345", config.ManualCommandExample, StringComparison.Ordinal);
        Assert.False(config.HostAutoDetected);
        Assert.Null(config.HostWarning);
    }

    [Fact]
    public void ApplyUbaSnapshot_GeneratesJoinUrlOnlyForUbaBuilds()
    {
        var service = new UbaAgentService(new AppOptions
        {
            UbaRemoteAgentEnabled = true,
            UbaPublicHost = "192.168.10.20",
            UbaRemoteHost = "0.0.0.0",
            UbaPort = 1345,
            UbaAgentMaxIdleSeconds = 180,
            UbaAgentStoreCapacityGb = 64,
            UbaMaxWorkers = 6
        });

        var ubaBuild = new BuildRecord
        {
            Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            BuildAccelerator = BuildAccelerator.Uba
        };

        service.ApplyUbaSnapshot(ubaBuild);

        Assert.True(ubaBuild.UbaRemoteEnabled);
        Assert.Equal("192.168.10.20", ubaBuild.UbaHost);
        Assert.Equal("0.0.0.0", ubaBuild.UbaListenHost);
        Assert.Equal(1345, ubaBuild.UbaPort);
        Assert.Equal(180, ubaBuild.UbaAgentMaxIdleSeconds);
        Assert.Equal(64, ubaBuild.UbaAgentStoreCapacityGb);
        Assert.Equal(6, ubaBuild.UbaMaxWorkers);
        Assert.Equal(
            "uba-agent://join?host=192.168.10.20&port=1345&buildId=aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa&maxIdle=180",
            ubaBuild.UbaAgentJoinUrl);
        Assert.Equal(
            "UbaAgent.exe -host=192.168.10.20:1345 -dir=\"%LOCALAPPDATA%\\UnrealLocalBuild\\UbaAgent\\Cache\" -capacity=64 -maxidle=180 -summary -quiet",
            ubaBuild.UbaAgentManualCommand);
        Assert.False(ubaBuild.UbaHostAutoDetected);
        Assert.Null(ubaBuild.UbaHostWarning);

        var regularBuild = new BuildRecord
        {
            BuildAccelerator = BuildAccelerator.None
        };

        service.ApplyUbaSnapshot(regularBuild);

        Assert.False(regularBuild.UbaRemoteEnabled);
        Assert.Null(regularBuild.UbaHost);
        Assert.Null(regularBuild.UbaListenHost);
        Assert.Null(regularBuild.UbaPort);
        Assert.Null(regularBuild.UbaAgentMaxIdleSeconds);
        Assert.Null(regularBuild.UbaAgentStoreCapacityGb);
        Assert.Null(regularBuild.UbaMaxWorkers);
        Assert.Null(regularBuild.UbaAgentJoinUrl);
        Assert.Null(regularBuild.UbaAgentManualCommand);
        Assert.False(regularBuild.UbaHostAutoDetected);
        Assert.Null(regularBuild.UbaHostWarning);
    }

    [Fact]
    public void ApplyUbaSnapshot_AllocatesDistinctPortsFromPool()
    {
        var service = new UbaAgentService(new AppOptions
        {
            UbaRemoteAgentEnabled = true,
            UbaPublicHost = "192.168.10.20",
            UbaPort = 1345,
            UbaPortPoolSize = 2
        });

        var first = new BuildRecord { Id = Guid.NewGuid(), BuildAccelerator = BuildAccelerator.Uba };
        var second = new BuildRecord { Id = Guid.NewGuid(), BuildAccelerator = BuildAccelerator.Uba };

        service.ApplyUbaSnapshot(first);
        service.ApplyUbaSnapshot(second);

        Assert.Equal(1345, first.UbaPort);
        Assert.Equal(1346, second.UbaPort);

        var third = new BuildRecord { Id = Guid.NewGuid(), BuildAccelerator = BuildAccelerator.Uba };
        Assert.Throws<UbaPortUnavailableException>(() => service.ApplyUbaSnapshot(third));

        service.ReleasePortForBuild(first.Id);
        service.ApplyUbaSnapshot(third);
        Assert.Equal(1345, third.UbaPort);
    }

    [Fact]
    public async Task CreateAgentPackageAsync_IncludesAgentFilesAndProtocolInstaller()
    {
        var root = Path.Combine(Path.GetTempPath(), "backend-uba-agent-package-tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "UbaSource");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "UbaAgent.exe"), "agent");
        File.WriteAllText(Path.Combine(source, "UbaDetours.dll"), "detours");
        File.WriteAllText(Path.Combine(source, "UbaHost.dll"), "host");
        File.WriteAllText(Path.Combine(source, "ignored.pdb"), "debug symbols");

        var service = new UbaAgentService(new AppOptions
        {
            UbaPublicHost = "192.168.10.20",
            UbaAgentPackageSourcePath = source
        });

        var package = await service.CreateAgentPackageAsync(null, CancellationToken.None);

        using var archive = new ZipArchive(new MemoryStream(package.Bytes), ZipArchiveMode.Read);
        var names = archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal("uba-agent-win64.zip", package.FileName);
        Assert.Contains("UbaAgent.exe", names);
        Assert.Contains("UbaDetours.dll", names);
        Assert.Contains("UbaHost.dll", names);
        Assert.Contains("install-uba-agent-protocol.ps1", names);
        Assert.Contains("README.txt", names);
        Assert.DoesNotContain("ignored.pdb", names);

        var readme = await ReadEntryAsync(archive, "README.txt");
        Assert.Contains("UbaAgent.exe -host=192.168.10.20:1345", readme, StringComparison.Ordinal);

        var installer = await ReadEntryAsync(archive, "install-uba-agent-protocol.ps1");
        Assert.Contains("HKCU:\\Software\\Classes\\uba-agent", installer, StringComparison.Ordinal);
        Assert.Contains("Start-UbaAgentFromProtocol.ps1", installer, StringComparison.Ordinal);
        Assert.Contains("uba-agent-protocol.log", installer, StringComparison.Ordinal);
        Assert.Contains("Write-UbaAgentLog", installer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAgentPackageAsync_ThrowsWhenAgentExecutableMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "backend-uba-agent-package-tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "UbaSource");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "UbaHost.dll"), "host");

        var service = new UbaAgentService(new AppOptions
        {
            UbaAgentPackageSourcePath = source
        });

        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.CreateAgentPackageAsync(null, CancellationToken.None));
        Assert.Contains("UbaAgent.exe", ex.Message, StringComparison.Ordinal);
    }

    private static async Task<string> ReadEntryAsync(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName) ?? throw new InvalidOperationException($"Missing zip entry {entryName}.");
        using var reader = new StreamReader(entry.Open());
        return await reader.ReadToEndAsync();
    }
}
