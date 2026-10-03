# USB Guardian

USB Guardian centrally controls removable storage and portable-device access on
Windows endpoints. Policy is managed from a WPF dashboard, persisted by an
ASP.NET Core server, and enforced locally by a Windows service.

## Components

| Project | Purpose |
|---|---|
| `USBGuardianServer` | REST API, SignalR hub, policy persistence, event history, CEF and SMTP sinks |
| `USBGuardianAgent` | LocalSystem worker that enforces policy with WMI and `pnputil.exe` |
| `USBGuardianControl` | WPF administrator dashboard |
| `USBGuardianNotifier` | WPF user-session alert shown when a removable device is blocked |
| `USBGuardianTests` | Regression tests for policy persistence, expiry, and device matching |

The server defaults to `http://0.0.0.0:5050`. The address and port are
configurable through `Http:BindAddress` and `Http:Port`. Set
`Http:Enabled=false` after configuring HTTPS to run without a plaintext listener.

## Enforcement Model

The agent manages these targets:

- Removable mass storage using `USBSTOR`, plus USB UASP transport devices using `UASPStor`
- MTP and portable devices such as phones and cameras
- Optional optical, Bluetooth, and Wi-Fi sectors
- Newly enumerated keyboard devices when BadUSB auto-block is enabled

USB hubs and host controllers are displayed for inventory only. They are never
disabled by normal policy enforcement because a hub may also carry the user's
keyboard and mouse.

When the removable-device sector is locked, exact device IDs and VID/PID or
VEN/PROD model rules determine which devices remain usable. Unlocking the sector
does not erase those rules. Timed unlocks contain a persisted UTC expiry and
therefore cannot become permanent after an agent or server reconnect.

The agent caches the latest policy under
`C:\ProgramData\USBGuardian\agent_policy_cache.json` and enforces it while the
server is unavailable. Learn mode is fail-closed: it is active only while the
control connection is healthy and its expiry is still in the future.
The server separately persists endpoint identity, address, last-seen time, and
last reported device count in `server_inventory.json`. Offline machines remain
visible in the dashboard, and saved policy changes apply when they reconnect.
For removable devices, optional sectors, and BadUSB keyboards, the agent records
the hardware it disabled in `agent_managed_state.json`. It restores only that
hardware when policy permits it, so devices disabled independently by Windows or
an administrator stay disabled. Security events are durably queued in
`agent_event_queue.json` until the Server accepts their stable event IDs.

Device arrival/removal events wake enforcement through WMI after a short PnP
debounce. A five-second periodic pass remains as a recovery fallback if event
delivery is unavailable or an event is missed.

## Build And Test

All projects target .NET 8. Endpoint enforcement requires Windows build 19041
or newer (Windows 10 version 2004, Windows 11, or Windows Server 2022+).
This is a capability floor; deploy on a supported, patched Windows edition.

```bat
dotnet build "USB Blocker.sln" -c Release
dotnet test USBGuardianTests\USBGuardianTests.csproj -c Release
```

Windows is required for WPF execution, Windows-service operation, WMI device
enumeration, and `pnputil` hardware tests. The server and pure policy tests can
also be compiled on another OS with `-p:EnableWindowsTargeting=true`.

## Deployment

Run the scripts from an elevated Command Prompt.

1. Run `Install-Server.bat` on the management machine.
2. Change the default `admin` password from
   a dashboard running on the Server machine.
3. Enroll each endpoint with its own credential using
   [`docs/EndpointEnrollment.md`](docs/EndpointEnrollment.md), then install its Agent.
4. Run `Start-Control.bat`, connect to the server, and verify the endpoint.

`Install-Agent.bat` publishes both the service and notifier. The notifier is
registered under the machine-wide Windows `Run` key so it starts in each user's
interactive session. `Update-All.bat` publishes all four runtime projects.
`Uninstall-All.bat` and Endpoint MSI removal invoke the Agent's recovery mode
before deleting the service. Only devices recorded as disabled by USB Guardian
are restored; failed device IDs stay in the managed-state file for recovery.

The server installer stores a protected copy of the generated agent token at
`C:\ProgramData\USBGuardian\agent_token.txt`. Server-side policies, events, and
credentials remain under `C:\ProgramData\USBGuardian`.

For managed deployment, run
`USBGuardianInstaller\Build-Installers.ps1` on Windows. It creates separate
self-contained x64 MSI packages for the Server, Endpoint, and Control dashboard.
The Endpoint MSI accepts `SERVERURL`, optional `MACHINEID`, and its enrolled
`AGENTTOKEN`. The Server MSI's `AGENTTOKEN` is only a legacy bootstrap secret. See
[`docs/MSI_GPO_DEPLOYMENT.md`](docs/MSI_GPO_DEPLOYMENT.md).

## Security Notes

- The admin password is stored as a BCrypt hash.
  New/rotated passwords use BCrypt's enhanced SHA-384 pre-hashing so the complete
  password matters, including long or Unicode passphrases. Existing classic hashes
  remain compatible until rotation; rotate administrator passwords after upgrade.
- The initial `admin` password can be changed only over a loopback connection.
  All remote admin API access remains rejected until that first rotation succeeds.
- Agents authenticate with per-machine enrolled credentials. Shared-token migration
  requires explicit `Security:AllowLegacySharedAgentToken=true`; it cannot authenticate
  an enrolled identity. Unauthenticated development requires the explicit
  `Security:AllowUnauthenticatedAgents` switch and an empty legacy AgentSecret.
- Admin API requests are limited per source IP to 60 requests per minute.
- HTTP remains enabled for LAN compatibility. Use HTTPS and set
  `Http:Enabled=false` and `Security:RequireHttps=true` for production. See the
  secure configuration template and enrollment guide.
- CORS is disabled unless explicit origins are configured.

For release signing, run `USBGuardianInstaller\Build-Installers.ps1 -Production
-CertificateThumbprint <thumbprint> -Version <version>`. Production mode refuses
unsigned payloads and verifies their signatures. The output includes a Bundle
with its exact MSIs, hashes, enrollment helper and updated instructions.
The current .NET 8 target must migrate before its support ends on 10 November 2026;
self-contained packages must be rebuilt to carry runtime security updates.

## Reference Package

`MY_USB-Lock-RP200v13888` is a third-party binary/documentation reference, not a
source dependency. Its stable operational patterns were evaluated without
executing its installers or copying proprietary code. See
[`docs/REFERENCE_COMPARISON.md`](docs/REFERENCE_COMPARISON.md) for the resulting
feature and safety decisions.

## Hardware Acceptance Gate

A successful build does not prove Windows device behavior. Before production,
test on representative Windows 10/11 hardware while the agent runs as
LocalSystem:

1. Lock with a keyboard and mouse attached, then insert an unauthorized USB
   drive. Only the storage device should be disabled.
2. Allow the exact device, then relock. It should remain usable.
3. Unplug and reinsert it; enforcement must stay idempotent without repeated
   reconnect loops.
4. Run a short timed unlock, restart the agent, and verify it relocks at expiry.
5. Disconnect the server during learn mode and verify the endpoint immediately
   returns to protected behavior.
6. Validate optical, Bluetooth, Wi-Fi, BadUSB, notifier, and uninstall recovery
   on hardware approved for those tests.
