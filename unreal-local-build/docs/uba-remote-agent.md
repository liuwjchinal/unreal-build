# UBA Remote Agent

This feature lets another Windows x64 machine join the current Unreal Build Tool UBA compile stage from the build detail page.

## Runtime Flow

- The build machine starts UBT with UBA remote hosting enabled.
- The build record stores a UBA snapshot: public host, listen host, port, agent cache capacity, max idle seconds, join URL, and manual command.
- A helper machine downloads the UBA Agent package from the web UI and installs the `uba-agent://` protocol handler.
- Clicking `Accelerate Build` opens the build-specific `uba-agent://join?...` URL on the helper machine.
- The protocol handler starts `UbaAgent.exe` with `-host=<build-host>:<port>` and exits after `-maxidle` seconds of inactivity.

## Configuration

Backend settings live under `App`:

- `UbaRemoteAgentEnabled`: enables the remote agent UI and snapshots for UBA builds.
- `UbaRemoteHost`: listen address passed to UBT as `-UBAHost`; default is `0.0.0.0`.
- `UbaPublicHost`: LAN address exposed to helper machines. Set this explicitly when auto-detection chooses the wrong NIC.
- `UbaPort`: first TCP port for UBA Host; default is `1345`.
- `UbaPortPoolSize`: number of sequential ports reserved for queued/running UBA builds; default is `16`.
- `UbaAgentMaxIdleSeconds`: idle timeout passed to remote agents; default is `120`.
- `UbaAgentStoreCapacityGb`: agent cache capacity and UBT store capacity; default is `40`.
- `UbaAgentPackageSourcePath`: optional override for the package source folder. If empty, the project engine root is used.

When multiple UBA builds are queued or running, the service reserves `UbaPort + offset` per build. If the pool is exhausted, the build request is rejected with a validation error instead of starting a conflicting UBA Host.

## Package Requirements

The package endpoint reads:

```text
<EngineRoot>\Engine\Binaries\Win64\UnrealBuildAccelerator\x64
```

The zip includes only the required runtime files and helper scripts. `UbaAgent.exe` is mandatory; if it is missing, the endpoint returns `404` with a clear error. Packages are cached in memory by source path and file timestamps.

## Diagnostics

The installed protocol handler writes logs on helper machines to:

```text
%LOCALAPPDATA%\UnrealLocalBuild\UbaAgent\Logs\uba-agent-protocol.log
```

Use this log to diagnose missing URL parameters, missing `UbaAgent.exe`, existing agent reuse, or process start failures.

## Network And Security Notes

- Open the selected TCP ports on the build machine firewall. With defaults, allow `1345-1360`.
- This implementation is LAN-trust only and has no token, user isolation, or authorization boundary.
- Anyone who can access the build detail page can copy the join URL and attempt to connect a helper machine.
- Installing the protocol handler lets the browser launch the local PowerShell launcher for `uba-agent://` URLs. Only install it on trusted helper machines.
- UBA only accelerates UBT C++ compilation. Cook, Pak, IoStore, archive, and zip stages are not distributed.
