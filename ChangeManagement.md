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
- **Architecture Pivot**: Modified backend WMI sweep to strictly target physical `USB Root Hub` and `eXtensible Host Controller` ports rather than dynamic flash drives.
- Identified that Master "Unlock All" rules conflicted with granular Blacklists. Rewrote `ConfigManager` to explicitly wipe Local Blacklists on `Unlock All` and Local Whitelists on `Lock System`.
- Identified that `PowerShell` execution caused background Deadlocks (`Generic Failure`) on Windows 11 when disabling physical Root Hubs.
  - Rewrote the Service Engine to execute native `pnputil.exe` commands.
  - Moved hardware execution to purely asynchronous background threads (`Task.Run`).
- Executed native administrative scripts to flush legacy device crash states.
- Cleansed source code structure of all temporary diagnostic and logging scripts.

## Phase 4: Security Hardening — Change Password & Force-Eject
- **Change Password Feature**: Added `CHANGEPASSWORD` command to the Named Pipe protocol and `ChangePassword()` to `ConfigManager`. Admins can now change the BCrypt password in-app without editing `config.json` manually.
- **ChangePasswordWindow Popup**: Created a dedicated `ChangePasswordWindow.xaml` modal dialog in the Admin UI with New Password + Confirm fields, inline validation errors, and a success MessageBox.
- **Removed Inline Password Panel**: Replaced the broken inline Change Password section with a cleaner `⚙ Change Password` button in the auth row that opens the popup.
- **UI Layout Fix**: Corrected broken `Grid.Row` numbering in `MainWindow.xaml` that caused "System Master Policy" and "Granular Device Control" labels to overlap.
- **Update-Service.bat Enhancement**: Updated the update script to also publish the `USBGuardianControl` UI to `Dist\Control\`, ensuring both binaries are always in sync after a rebuild.

## Phase 5: Two-Layer Lock Enforcement & Re-enable Fix
- **Force-Eject on Lock**: Identified security gap — disabling Root Hubs did not eject already-mounted USB storage devices. Added `ForceEjectActiveUsbDevices()` that queries `USBSTOR%` (flash drives, HDDs) and USB MTP/phone devices and disables each individually when the system is locked.
- **Force Re-enable on Unlock**: Identified follow-on issue — `pnputil /disable-device` on `USBSTOR` devices persists even after re-enabling the hub, so USB drives could not reconnect after unlocking. Added symmetric `ForceEnableUsbDevices()` that re-enables any disabled USBSTOR/MTP devices when the system is unlocked.
- **Refactored to `ApplyToUsbChildDevices()`**: Both eject and enable methods consolidated into a single parameterized helper, reducing code duplication.
- **`Enforce()` is now fully symmetric**: Locked state → eject child devices. Unlocked state → re-enable child devices.

## Phase 6: UI — Connected Device Indicators & PC Name
- **PC Hostname in Header**: `MainWindow.xaml` now shows `🖥 MACHINE_NAME` below the panel title, so admins instantly know which machine they are managing.
- **`UsbDeviceInfo` Model Extended**: Added `IsHub` (bool) and `ConnectedDeviceName` (string) fields to the service's device model.
- **`GetConnectedDevices()` Extended**: Now runs a second WMI query after the hub sweep to find actively connected `USBSTOR` and MTP devices, returning them as separate entries with `IsHub = false`.
- **Dual-Template Device List**: `MainWindow.xaml` device list now renders two row types:
  - **Hub Row** (`⬡`) — shows port name, PNP ID, hardware status, port policy, and Allow/Block buttons.
  - **Connected Device Row** (`🔌`) — indented below hubs, shows device name and a colour-coded `● CONNECTED` / `○ BLOCKED / EJECTED` badge.
- **Section Label Updated**: "Granular Device Control" renamed to "USB Ports & Connected Devices".
- **`UsbDeviceViewModel` Updated**: Extended with `IsHub`, `HubRowVisibility`, `DeviceRowVisibility`, `ConnectedLabel`, `ConnectedColor` computed properties.
