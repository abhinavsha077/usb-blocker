# USB Guardian — Change Management

This document tracks the development history of USB Guardian across all phases from initial prototype to the current v2 centralized network architecture.

---

## Phase 1: Core Service Initialization

- Initialized `.NET 8 Worker Service` (`USBGuardianService`).
- Implemented core locking mechanism via Registry (`HKLM\SYSTEM\CurrentControlSet\Services\USBSTOR`).
- Created Named Pipe IPC server (`USBGuardianControlPipe`) to listen for secure commands.
- Implemented BCrypt password hashing and configuration management (`config.json`).
- Built the `.NET 8 WPF` admin Control Panel with password authentication.

---

## Phase 2: Granular Device Control Migration

- Transitioned away from generic `USBSTOR` registry edits.
- Integrated native `WMI (Win32_PnPEntity)` queries to enumerate connected devices.
- Attempted initial device-level blocks using PowerShell (`Disable-PnpDevice`, `Enable-PnpDevice`).
- Refactored Control Panel to visualize an active list of connected hardware with individual Allow/Block toggles.

---

## Phase 3: Hub-Level Precision and Reliability

- Fixed UI bugs regarding list layout and text wrapping on smaller resolutions.
- Discovered "disappearing devices" bug: disabling individual flash drives unmounted them from the PnP tree, breaking the UI list.
- **Architecture Pivot**: Modified backend WMI sweep to target physical USB Root Hubs and eXtensible Host Controllers rather than dynamic flash drives.
- Identified that PowerShell caused background deadlocks (`Generic Failure`) on Windows 11 when disabling physical Root Hubs.
  - Rewrote the Service Engine to execute native `pnputil.exe` commands.
  - Moved hardware execution to asynchronous background threads (`Task.Run`).

---

## Phase 4: Security Hardening — Change Password & Force-Eject

- Added `CHANGEPASSWORD` command to the Named Pipe protocol and `ChangePassword()` to `ConfigManager`.
- Created `ChangePasswordWindow.xaml` modal dialog with validation.
- Replaced broken inline password panel with a `⚙ Change Password` button.
- Fixed broken `Grid.Row` numbering in `MainWindow.xaml`.
- Updated `Update-Service.bat` to publish both the service and Control UI binaries in sync.

---

## Phase 5: Two-Layer Lock Enforcement & Re-enable Fix

- **Force-Eject on Lock**: Added `ForceEjectActiveUsbDevices()` targeting `USBSTOR%` and USB MTP/phone devices — disables each individually when the system is locked.
- **Force Re-enable on Unlock**: Added `ForceEnableUsbDevices()` that re-enables any disabled USBSTOR/MTP devices when unlocked.
- Refactored both into a single parameterized `ApplyToUsbChildDevices()` helper.
- `Enforce()` is now fully symmetric: locked → eject child devices; unlocked → re-enable child devices.

---

## Phase 6: UI — Connected Device Indicators & PC Name

- Added PC hostname display in the header (`🖥 MACHINE_NAME`).
- Extended `UsbDeviceInfo` model with `IsHub` (bool) and `ConnectedDeviceName` (string).
- Extended `GetConnectedDevices()` with a second WMI query for connected `USBSTOR` and MTP devices.
- Added dual-template device list in `MainWindow.xaml`:
  - **Hub Row** (`⬡`) — port name, PNP ID, hardware status, policy, Allow/Block buttons.
  - **Connected Device Row** (`🔌`) — indented, device name, coloured connected/blocked badge.

---

## Phase 7: Centralized Network Architecture (v2)

- **Architecture Pivot**: Transitioned from standalone single-PC service to a Hub/Agent network model.
- **`USBGuardianServer` Created**: ASP.NET Core 8 Web API running as a Windows Service on port 5050.
  - REST API for the Control UI (lock/unlock, allow/block, change password).
  - SignalR WebSocket Hub for real-time Agent connections.
  - Central BCrypt password (`server_config.json`) and per-machine policies (`server_policies.json`).
- **`USBGuardianAgent` Created**: Cloned and modified from the original LocalService.
  - Removed local password and NamedPipe logic.
  - Implemented `AgentSignalRClient` for persistent WebSocket connection to the Server.
  - Receives policies exclusively from the Server; enforces them locally via WMI/pnputil.
- **`USBGuardianControl` Rewritten**: Manages multiple machines simultaneously.
  - HTTP REST client replaces Named Pipe client.
  - Left-pane ListBox shows all connected Agents with Online/Offline and Locked/Open status.
  - Main view dynamically binds to the selected Agent's USB hardware.
- **Deployment Scripts Refactored**:
  - `Install-Server.bat` — builds Server, opens firewall port 5050, registers service.
  - `Install-Agent.bat` — silent .NET 8 install, prompts for Server IP, drops `agent_config.json`, registers agent service.
  - `Update-All.bat` — compiles and restarts all 3 components.

---

## Phase 8: Hardening & Bug Fixes

- **UI Crash on Startup**: Fixed `InvalidOperationException` where `HttpClient.BaseAddress` was modified after the first request. Fixed by recreating `HttpClient` on every reconnect.
- **Connection Exception Handling**: Wrapped all UI async events in `try-catch`, rendering errors in the Status Bar rather than crashing the app. Added WPF `DispatcherUnhandledException` global handler.
- **Timer Race Condition**: Fixed auto-refresh timer firing before the server connection was established.
- **USB Reconnect Popup Loop**: Fixed `ForceEnableUsbDevices()` calling `pnputil /enable-device` on already-mounted drives every 5 seconds (causing the USB arrival sound + AutoPlay popup each cycle).
  - Added `_prevAnyBlocked` state tracker in `UsbPolicyManager.cs` — enforcement only fires on lock/unlock state transitions.
- **UI Badge Sync**: Changed `ConnectedLabel` logic to read from policy `IsAllowed` instead of hardware `Status`, so badges update immediately from server intent rather than lagging behind WMI hardware state.

---

## Phase 9: USB Detection Rewrite & UI Overhaul (v2.1)

### USB Detection Rewrite (`UsbPolicyManager.cs`)

**Problem**: The WMI query `Win32_PnPEntity WHERE Name LIKE '%Hub%' OR Name LIKE '%Host Controller%'` matched device names as strings, causing partial or zero detection on many hardware configurations (OEM firmware, Thunderbolt controllers, enterprise hardware, AMD platforms) where device names don't contain those substrings.

**Fix**: Replaced all name-based WMI queries with dedicated WMI device classes:
- `Win32_USBHub` — enumerates all USB hubs by device class regardless of vendor name. Finds root hubs (`USB\ROOT_HUB30\...`, `USB\ROOT_HUB20\...`) and generic hubs universally.
- `Win32_USBController` — enumerates all xHCI/eHCI/uHCI host controllers (PCI-attached) regardless of manufacturer naming.

**Threading fix**: The `_lock` mutex was held for the entire duration of `Enforce()`, including all `pnputil` subprocess waits (up to 5s each). With multiple hubs this could block incoming `ApplyRemotePolicy` SignalR calls for 20–30 seconds. Fixed by:
- Collecting required pnputil commands (device IDs + verbs) inside the lock as a snapshot list.
- Releasing the lock, then executing all pnputil calls outside it.

**Other improvements**:
- Added private `IsUnlockedCore()` for use inside locked sections, avoiding redundant re-locking.
- Increased pnputil subprocess timeout from 3s to 5s for slower enterprise hardware.
- `GetConnectedDevices()` now takes a policy snapshot at the start (one lock acquire) instead of per-device lock acquisitions.

### Admin UI Overhaul (`USBGuardianControl`)

- **Settings persistence** (`ControlSettings.cs`): Server URL saved to `%AppData%\USBGuardian\control_settings.json` and loaded on startup — no more retyping the address each launch.
- **Connection status badge**: Real-time `● Connected to http://...` (green) / `○ Not connected` (grey) indicator in the header.
- **Preset timed-unlock buttons**: Quick-select row with 15 min / 30 min / 1 hr / 4 hr / 8 hr presets plus a custom duration input. Replaces the bare text box.
- **Colored status bar**: Success (green), Error (red), Warning (yellow), Info (grey) — errors are no longer invisible in a single grey bar.
- **Empty states**: Clear guidance messages when no agents are connected and when no machine is selected.
- **Confirmation dialogs**: Lock All and Unlock All now require a `MessageBox.Show` `Yes/No` confirmation to prevent accidental global commands.
- **Global buttons disabled until connected**: Lock All and Unlock All are greyed out until a successful server connection is established.
- **Per-machine buttons disabled until selected**: Lock, Unlock, and all timed-unlock controls are greyed out until a machine is selected from the left panel.
- **Auto-refresh indicator**: `⟳ Auto-refresh: ON` label activates in the header after a successful connect.

### Project Cleanup

- Removed generated/junk files: `filestats.txt`, `linecounts.txt`, `query`, `reflog.txt`, `*.resolved`, `usb_devices_present.json`, `usb_status.json`, `count-lines.ps1`.
- Removed stale compiled artifact directories: `Dist/Dist_bak/`, `Dist/Service/`.
- Removed legacy v1-only deployment scripts: `Install-Background-Service.bat`, `Uninstall-Background-Service.bat`, `Start-Service.bat`, `Update-Service.bat`.
- Moved documentation to `docs/` folder.
- Added `CLAUDE.md` with build commands, architecture overview, and development constraints for AI-assisted development.

---

## Phase 10: Reliability, Security, And Managed Deployment

- Reworked Agent/Server reconnect and authentication handling, persisted timed
  policy state, and added offline policy caching and queued security events.
- Replaced polling-only enforcement with WMI device-change notifications plus a
  periodic recovery scan.
- Added ACL-protected atomic writes for server credentials, policy, inventory,
  event data, and endpoint policy state.
- Added a bounded disk-backed Agent event outbox with occurrence timestamps and
  Server-side ID deduplication so audit events survive reconnects and restarts.
- Added a persistent ownership ledger for removable, sector, and BadUSB device
  actions. Policy changes and uninstall now restore only hardware USB Guardian
  actually disabled.
- Added failed-action retry backoff, explicit failure audit events, and durable
  service recovery actions for both Windows services.
- Made missing Agent credentials fail closed, restricted the initial admin
  password rotation to loopback, added per-IP API limits, and enabled
  configurable HTTPS-only listening.
- Added persistent endpoint inventory so disconnected machines retain identity,
  last-seen metadata, and policy management in the dashboard.
- Replaced stale single-package WiX authoring with separate Server, Endpoint,
  and Control MSI projects, service recovery, GPO properties, upgrade handling,
  optional signing, and SHA-256 manifests.
- Added regression coverage and Windows CI for solution build, tests, and MSI
  packaging, including transitive NuGet vulnerability auditing and clean
  versioned package outputs. Hardware enforcement and MSI lifecycle
  qualification remain Windows acceptance gates.

### 3 October 2026 — Readiness follow-up

- Corrected webcam/portable classification, long-password hashing, administrator
  redirect handling, protected endpoint configuration writes and runtime ownership.
- Fixed notifier inbox delivery across sessions, ownership cleanup verification,
  audit identity collisions/retention, Windows capability checks and versioned MSI
  linking. Added production signing enforcement and current enrollment bundles.
- Final automated evidence: 109 regression tests and 34 integration probes pass;
  three unsigned 1.1.4 acceptance candidates build successfully. Physical hardware,
  WPF, LocalSystem deployment, TLS trust, signing and installer lifecycle remain
  release gates. See [readiness follow-up](production-review-2026-10-03/readiness-followup.md).
