# AGENTS.md

This file provides guidance to Codex (Codex.ai/code) when working with code in this repository.

## Build Commands

All projects target **.NET 8**. Run from the solution root unless otherwise noted.

```bash
# Build entire solution
dotnet build "USB Blocker.sln"

# Build a single project
dotnet build USBGuardianServer/USBGuardianServer.csproj
dotnet build USBGuardianAgent/USBGuardianAgent.csproj
dotnet build USBGuardianControl/USBGuardianControl.csproj
dotnet build USBGuardianNotifier/USBGuardianNotifier.csproj
dotnet test USBGuardianTests/USBGuardianTests.csproj

# Windows only: self-contained Server, Endpoint, and Control MSI packages
powershell -ExecutionPolicy Bypass -File USBGuardianInstaller/Build-Installers.ps1 -Version 1.1.0

# Publish (what Install-*.bat scripts do) — outputs to Dist/
dotnet publish USBGuardianServer/USBGuardianServer.csproj   -c Release -o Dist/Server
dotnet publish USBGuardianAgent/USBGuardianAgent.csproj     -c Release -o Dist/Agent
dotnet publish USBGuardianControl/USBGuardianControl.csproj -c Release -o Dist/Control
dotnet publish USBGuardianNotifier/USBGuardianNotifier.csproj -c Release -o Dist/Notifier  # deploy to endpoints; run at logon

# Run server locally (not as service)
dotnet run --project USBGuardianServer/USBGuardianServer.csproj

# Run agent locally (requires LocalSystem for WMI/pnputil — use an elevated terminal)
dotnet run --project USBGuardianAgent/USBGuardianAgent.csproj

# Launch admin UI
dotnet run --project USBGuardianControl/USBGuardianControl.csproj
```

`USBGuardianTests` covers device-ID matching and server policy persistence/expiry.
It also checks configuration failover and the security/lifecycle invariants in
the WiX authoring. Windows hardware behavior still requires the manual acceptance
gate in `README.md`.

## Deployment Scripts

| Script | What it does |
|---|---|
| `Install-Server.bat` | Publish server → open firewall port 5050 → register + start Windows Service |
| `Install-Agent.bat` | Verify .NET 8 SDK → prompt for server/token → publish Agent + Notifier → register service and user-session notifier |
| `Update-All.bat` | Stop services and interactive apps → rebuild all four runtime projects → restart installed services |
| `Start-Control.bat` | Launch admin WPF dashboard |
| `Uninstall-All.bat` | Remove server + agent Windows services |
| `USBGuardianInstaller\Build-Installers.ps1` | Publish self-contained x64 payloads, build three WiX MSIs, optionally Authenticode-sign, and emit SHA-256 hashes |

Run all `.bat` scripts **as Administrator** — they use `sc`, `netsh`, and `pnputil`.

## Architecture

Three-tier network model over a LAN:

```
USBGuardianControl (WPF)
    ↕  HTTP REST  (port 5050, X-Admin-Password header)
USBGuardianServer (ASP.NET Core — Windows Service, LocalSystem)
    ↕  SignalR WebSocket  (/agenthub)
USBGuardianAgent  (.NET Worker Service — Windows Service, LocalSystem, one per endpoint PC)
    ↕  WMI + pnputil.exe
USB hardware (Root Hubs, Host Controllers, USBSTOR, MTP)
    │  (agent, on block) writes alert JSON → C:\ProgramData\USBGuardian\alerts\
    ↓
USBGuardianNotifier (WPF, runs in the interactive USER session, launched at logon)
    → FileSystemWatcher shows a full-screen SECURITY ALERT to the logged-in user
```

### USBGuardianServer
- Listens on `http://0.0.0.0:5050`
- `AgentHub` (SignalR) maintains an in-memory `ConcurrentDictionary<connectionId, AgentInfo>`. Agents register on connect and push device status every 5 s.
- `AgentController` exposes all REST endpoints (`/api/agents/...`). Every request validates `X-Admin-Password` against a BCrypt hash; admin API traffic is rate-limited per source IP.
- `PolicyStore` persists per-machine policies to `C:\ProgramData\USBGuardian\server_policies.json`.
- `ServerConfigManager` manages the BCrypt password hash in `server_config.json`.

### USBGuardianAgent
- `Worker` (BackgroundService) runs enforcement immediately on WMI device-change
  events and at least every 5 seconds as a fallback, then pushes device status.
- `UsbPolicyManager` is the core enforcement engine: WMI queries enumerate USB hardware; `pnputil.exe` is invoked directly with structured arguments, timeout handling, and exit-code checks.
- `AgentSignalRClient` maintains a persistent reconnecting WebSocket to the server. It receives persisted policy updates, including unlock and learn-mode expiries. The `TimedUnlock` handler remains only for compatibility with older servers.
- Agent config is read from `C:\ProgramData\USBGuardian\agent_config.json`
  (ServerUrl, MachineId, AgentToken). If that file is absent, MSI deployments
  read the protected `HKLM\SOFTWARE\USBGuardian\Agent` values.

### USBGuardianControl
- WPF app with no external NuGet dependencies.
- `ServerClient` wraps all HTTP calls. `MainWindow.xaml.cs` contains all UI logic. `AgentViewModel` / `UsbDeviceViewModel` are thin computed-property wrappers over the API DTOs.
- A `DispatcherTimer` (5 s) auto-refreshes the agent and device lists after a successful Connect.

### USBGuardianNotifier
- WPF app that runs in the **interactive user session** (NOT a service — the session-0 agent
  cannot draw UI to the user). Deploy to auto-start at logon via a Scheduled Task
  (trigger: "At log on of any user") or an HKLM `...\Run` entry.
- Watches `C:\ProgramData\USBGuardian\alerts\*.json` (written by `UsbPolicyManager.WriteBlockAlert`
  on each fresh block) and shows a full-screen topmost alert (`AlertWindow`). 2s dwell before
  a click/Esc can dismiss (so an injected keystroke can't auto-close it); 12s auto-close.

Note: the legacy pre-V2 `USBGuardianService` project was removed from the repo and solution.

## Key Data Files at Runtime

| Path | Owner | Contents |
|---|---|---|
| `C:\ProgramData\USBGuardian\server_config.json` | Server | BCrypt admin password hash |
| `C:\ProgramData\USBGuardian\server_policies.json` | Server | Per-machine policy, allow/block lists, timed unlock and learn expiries |
| `C:\ProgramData\USBGuardian\server_inventory.json` | Server | Endpoint identity, address, last-seen time, and last reported device count |
| `C:\ProgramData\USBGuardian\agent_config.json` | Agent | ServerUrl + MachineId + AgentToken written by `Install-Agent.bat` |
| `C:\ProgramData\USBGuardian\agent_policy_cache.json` | Agent | Last policy for offline enforcement |
| `C:\ProgramData\USBGuardian\agent_managed_state.json` | Agent | Removable, sector, and BadUSB devices disabled by USB Guardian and eligible for restoration |
| `C:\ProgramData\USBGuardian\agent_event_queue.json` | Agent | Durable, bounded security-event outbox awaiting Server acknowledgement |
| `HKLM\SOFTWARE\USBGuardian\Server` | Server MSI | Protected AgentSecret bootstrap value |
| `HKLM\SOFTWARE\USBGuardian\Agent` | Endpoint MSI | Protected ServerUrl, optional MachineId, and AgentToken |
| `%AppData%\USBGuardian\control_settings.json` | Control UI | Saved server URL (`ControlSettings.cs` reads/writes on connect) |

## Important Constraints

- Both Server and Agent run as **LocalSystem** — required for WMI hardware access and `pnputil` device enable/disable. Do not change the service account.
- The server defaults to HTTP **5050** on all interfaces. `Http:Enabled`, `Http:BindAddress`, and `Http:Port` are configurable; changing the port requires a firewall rule update too. HTTPS is optional via `Https:Enabled`/`Https:Port`, and HTTP can be disabled after HTTPS is configured.
- `pnputil /disable-device` and `/enable-device` require the exact **PNPDeviceID** string as reported by WMI — preserve casing and formatting.
- **Enforcement model (P0.1, current):** blocking is **per-device and limited to removable storage (`USBSTOR%`) + MTP/portable devices**, matched via `Win32_PnPEntity`. The agent **never disables `Win32_USBHub` / `Win32_USBController` for enforcement** — doing so also disabled keyboards/mice sharing the hub. `Win32_USBHub`/`Win32_USBController` are still enumerated in `GetConnectedDevices()` for **UI display only**. There is an `IsHumanInterfaceDevice()` guard so an input device can never be disabled.
- Enforcement is **state-aware / idempotent**: a device is toggled only when its current WMI `Status` differs from the desired state, so pnputil never re-runs on an already-correct device (no repeated reconnect sound / AutoPlay popup). This replaced the old `_prevAnyBlocked` transition gate and the `ForceApplyToStorageDevices` helper.
- Device arrival/removal events wake enforcement through
  `Win32_DeviceChangeEvent` after a 250 ms PnP debounce. The 5-second scan remains
  a fallback if WMI event delivery fails or stops.
- SignalR policy pushes arrive on a thread-pool thread. The enforcement lock (`_lock`) must **not** be held while executing pnputil subprocesses — snapshot the policy under the lock, release, then run pnputil outside.
- Timed unlock and learn-mode expiry are persisted in policy. Learn mode is active only while the Agent is connected to the Server; offline behavior is fail-closed.
- **Security (P0.2/P0.3):** agents present a shared secret (`AgentConfig.AgentToken`) validated against the server's `AgentSecret`. An empty secret rejects registration unless `Security:AllowUnauthenticatedAgents=true` is explicitly set for development. While the admin password is the default (`admin`), its first rotation must come from loopback and all remote admin API access is refused.
- **Endpoint enrollment:** current agents use an administrator-issued per-machine
  `AgentToken`, persisted as a hash in `endpoint_credentials.json`. Shared-token
  fallback requires `Security:AllowLegacySharedAgentToken=true` and never applies
  to an enrolled identity. See `docs/EndpointEnrollment.md`. Production transport
  uses HTTPS with `Security:RequireHttps=true`; the default HTTP listener remains
  available for explicit LAN compatibility.
- **Quiet check runner:** Codex checks on this Windows host must use
  `exec_command` with `tty:true` and `login:false`. This avoids the repeated console
  flashes the user confirmed were caused by the check runner. Runtime subprocesses
  must also use absolute system executable paths, `CreateNoWindow=true`, and
  `WindowStyle=Hidden` with `UseShellExecute=false`.

## Documentation

Project documentation lives in [`docs/`](docs/):
- [`docs/ChangeManagement.md`](docs/ChangeManagement.md) — full development history (Phases 1–9).
