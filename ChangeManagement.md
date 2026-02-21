# USB Guardian - Change Management Document

## Phase 1: Core Service Initialization
- Initialized `.NET 8 Worker Service` (USBGuardianService).
- Implemented core locking mechanism via Registry (`HKLM\SYSTEM\CurrentControlSet\Services\USBSTOR`).
- Created Named Pipe IPC server (`USBGuardianControlPipe`) to listen for secure commands.
- Implemented BCrypt password hashing and configuration management (`config.json`).
- Built the `.NET 8 WPF` admin Control Panel with password authentication.

## Phase 2: Granular Device Control Migration
- Transitioned away from generic `USBSTOR` registry edits.
- Researched and integrated native `WMI (Win32_PnPEntity)` queries to enumerate connected devices.
- Attempted initial device-level blocks using `PowerShell` Applets (`Disable-PnpDevice`, `Enable-PnpDevice`).
- Refactored Control Panel to visualize an active list of connected hardware with individual Allow/Block toggles.
- Encapsulated service installation into `Install-Background-Service.bat`.

## Phase 3: Hub-Level Precision and Reliability Enhancements
- Fixed UI bugs regarding list layout and text wrapping on smaller resolutions.
- Discovered "disappearing devices" bug: Disabling individual flash drives unmounted them from the PnP tree, breaking the UI list.
- **Architecture Pivot**: Modified backend WMI sweep to strictly target physical `USB Root Hub` and `eXtensible Host Controller` ports rather than dynamic flash drives. This allows permanent Port-Level lockdown without UI jitter.
- Identified that Master "Unlock All" rules conflicted with granular Blacklists.
  - Rewrote the core `ConfigManager` to explicitly wipe Local Blacklists on `Unlock All` and explicitly wipe Local Whitelists on `Lock System`.
- Identified that `PowerShell` execution caused background Deadlocks (`Generic Failure`) on Windows 11 when disabling physical Root Hubs.
  - Rewrote the Service Engine to execute native `pnputil.exe` commands under the hood, perfectly bypassing WMI thread lockups.
  - Moved hardware execution to purely asynchronous background threads (`Task.Run`), resolving the "Sending UNLOCK..." UI freeze bug.
- Executed native administrative scripts to successfully flush legacy device crash states, recovering natively disabled flash drives.
- Cleansed the source code structure of all temporary diagnostic and logging scripts.
