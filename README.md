# USB Guardian Endpoint Security

USB Guardian is a robust, system-level security application designed to explicitly manage and lock down physical USB ports on Windows environments. It prevents unauthorized data exfiltration via USB Mass Storage, Phones, and MTP devices by electronically disabling Root Hub ports at the driver level — and also force-ejects any devices that are already connected when a lock command is issued.

## Architecture

The project is split into three main components:

1. **USBGuardianService (Core Daemon)**
   A detached `.NET 8 Worker Service` that runs natively as `LocalSystem`. It loops every 5 seconds continuously enforcing security rules. It listens for authorized commands passively via a Named Pipe.

2. **USBGuardianControl (Admin UI)**
   A secure `.NET 8 WPF` application that administrators use to authenticate (via BCrypt password) and transmit locking commands to the hidden Service daemon.

3. **System Configuration**
   Hashed passwords and Blacklist/Whitelist policies are stored dynamically in `C:\ProgramData\USBGuardian\config.json`.

## Technical Mechanics

### Native Hub-Level Blocking
Unlike older, unreliable methods that simply disable the `USBSTOR` registry key, USB Guardian targets the physical **USB Root Hub** network on your motherboard. It uses WMI (`Win32_PnPEntity`) to locate your physical hubs and executes native Windows `pnputil.exe` commands to cut the data/power pathways structurally.

### Force-Eject on Lock (Two-Layer Security)
When the system is locked, USB Guardian enforces a **two-layer** shutdown:
- **Layer 1** — Disables all USB Root Hubs (blocks new connections)
- **Layer 2** — Scans for and individually disables any `USBSTOR` (flash drives, HDDs) and MTP (phones, cameras) devices that are **already connected and mounted**, preventing mid-session data exfiltration

When unlocked, both layers are reversed — hubs are re-enabled and any previously force-ejected devices are automatically re-enabled within the next 5-second enforcement cycle.

### Asynchronous IPC
Communication between the Admin UI and Service is handled via Windows Named Pipes (`USBGuardianControlPipe`). Hardware execution runs in detached async Tasks inside C# to ensure the UI never freezes.

### Master Overrides
- **Unlock All Indefinitely**: Global ALLOW rule. Wipes the legacy blacklist.
- **Lock System (Enforce)**: Global BLOCK rule. Wipes the legacy whitelist. Immediately force-ejects connected devices.
- **Timed Unlock**: Temporarily unlocks for N minutes, then auto-relocks.
- **Granular Toggles**: Pick an individual USB Hub and explicitly allow or block it.

### Change Password (In-App)
Admins can change the BCrypt password directly from the Control Panel without touching the filesystem. A popup dialog authenticates the request over the Named Pipe and saves the new hash to `config.json` instantly.

## Deployment & Usage

### Installing the Service
1. Run `Install-Background-Service.bat` as Administrator.
2. The script registers `USBGuardianService.exe` as an auto-starting Windows Service running as LocalSystem.

### Controlling the Ports
1. Double-click `Start-Control.bat` (runs the UI as Administrator).
2. Enter the default password (`admin`).
3. View the live states of your motherboard Hubs and any connected devices.
4. Click Lock/Unlock, or target specific ports.

### Changing the Admin Password
1. Enter your current password in the main window and click **Connect & Refresh**.
2. Click **⚙ Change Password** (top-right of the auth row).
3. Enter and confirm the new password in the popup dialog.
4. Click **Change Password** — the new BCrypt hash is saved to `config.json` immediately.

### Updating / Recompiling
Double-click `Update-Service.bat` to gracefully halt the service, rebuild the entire `.sln`, copy new binaries for both the Service **and** the Control UI, and reboot the daemon.

## File Structure

### `USBGuardianService\` (Background Daemon)

| File | Purpose |
|------|---------|
| `ConfigManager.cs` | Parses `config.json`. Handles BCrypt password verification and `ChangePassword()`. |
| `PipeServer.cs` | Named Pipe IPC server. Handles: `LOCK`, `UNLOCK`, `STATUS`, `LIST`, `ALLOW`, `BLOCK`, `CHANGEPASSWORD`. |
| `UsbPolicyManager.cs` | Core engine. Hub-level `pnputil` enforcement, `ForceEjectActiveUsbDevices()`, `ForceEnableUsbDevices()`, and full device listing with `IsHub` flag. |
| `Worker.cs` | `.NET 8` BackgroundService loop — calls `Enforce()` every 5 seconds. |
| `Program.cs` | Dependency Injection bootstrapping. |

### `USBGuardianControl\` (Admin Panel)

| File | Purpose |
|------|---------|
| `MainWindow.xaml` | WPF dashboard — PC hostname header, auth row, master policy controls, dual-template device list (Hub rows + Connected Device rows). |
| `MainWindow.xaml.cs` | UI code-behind. Named Pipe client, auto-refresh timer (3 sec), all button handlers. |
| `ChangePasswordWindow.xaml` | Modal popup for changing the admin password. |
| `ChangePasswordWindow.xaml.cs` | Validates input, sends `CHANGEPASSWORD` over pipe, shows inline error or success message. |
| `App.xaml` / `.cs` | Application bootstrapping. |

### `.\` (Root Admin Scripts)

| File | Purpose |
|------|---------|
| `Install-Background-Service.bat` | Registers the service as auto-start under LocalSystem. |
| `Update-Service.bat` | Stops service → rebuilds & publishes **both** Service and Control UI → restarts service. |
| `Start-Control.bat` | Launches the Admin UI as Administrator. |
| `Uninstall-Background-Service.bat` | Stops and deletes the Windows service. |

## Complete Deployment Guide

**Step 1. Compilation**
Ensure **.NET 8 SDK** is installed. In the project root:
```cmd
dotnet build "USB Blocker.sln" -c Release
```

**Step 2. Daemon Configuration**
The default password is `admin`. It is BCrypt-hashed and stored in `C:\ProgramData\USBGuardian\config.json` on first run. Change it via the in-app **⚙ Change Password** dialog after first login.

**Step 3. Install the Windows Background Policy Engine**
Right-click `Install-Background-Service.bat` → **Run as Administrator**.
- Registers the executable under `services.msc` as `USB Guardian Service`
- Sets `Start=Auto` (enforced on every boot)
- Sets `obj=LocalSystem` (tamper resistant — cannot be killed by standard users)

**Step 4. Final Verification**
Open `Start-Control.bat`, login with `admin`, confirm you can see the USB Root Hubs listed with their policy states and any connected device cards below them.
