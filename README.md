# USB Guardian Endpoint Security

USB Guardian is a robust, system-level security application designed to explicitly manage and lock down physical USB ports on Windows environments. It enables administrators to prevent unauthorized data exfiltration via USB Mass Storage, Phones, and MTP devices by electronically shutting down the Root Hub ports on the motherboard.

## Architecture

The project is split into three main components:

1. **USBGuardianService (Core Daemon)**
   A detached `.NET 8 Worker Service` that runs natively as `LocalSystem`. It loops in the background continuously enforcing the security rules. It listens for authorized commands passively via a Named Pipe.

2. **USBGuardianControl (Admin UI)**
   A secure `.NET 8 WPF` application that administrators use to authenticate (via BCrypt password) and transmit locking commands to the hidden Service daemon. 

3. **System Configuration**
   The application stores its hashed passwords and Blacklist/Whitelist policies dynamically in `C:\ProgramData\USBGuardian\config.json`.

## Technical Mechanics

### Native Hub-Level Blocking
Unlike older, unreliable methods that simply disable the `USBSTOR` registry key, USB Guardian actually targets the physical **USB Root Hub** network on your motherboard. 
It uses WMI (`Win32_PnPEntity`) to locate your physical hubs and executes native Windows `pnputil.exe` commands to cut the data/power pathways structurally. This guarantees that devices (even smart MTP phones) cannot bypass the software block.

### Asynchronous IPC
Communication between the Admin UI and the Service is handled by Windows Named Pipes (`USBGuardianControlPipe`). The Service handles hardware execution in detached asynchronous Tasks inside C# to ensure the UI interface never hangs or freezes during driver toggles.

### Master Overrides
- **Unlock All Indefinitely**: Imposes a global "ALLOW" rule. Wipes the legacy blacklist.
- **Lock System (Enforce)**: Imposes a global "BLOCK" rule. Wipes the legacy whitelist.
- **Granular Toggles**: An Admin can pick an individual active USB Hub and explicitly allow or block it, bypassing the global rule.

## Deployment & Usage

### Installing the Service
To correctly install the hidden engine:
1. Run `Install-Background-Service.bat` as Administrator. 
2. The script will securely register `USBGuardianService.exe` as an auto-starting Windows Service running as LocalSystem.

### Controlling the Ports
1. Double-click `Start-Control.bat` (runs the UI as Administrator).
2. Enter the default password.
3. View the live states of your motherboard Hubs. Click Lock/Unlock, or target specific ports.

### Updating/Recompiling
If making modifications to the C# source:
1. Double-click `Update-Service.bat` to gracefully halt the service, copy the new bins, and reboot the daemon seamlessly.

## File Structure

The project is structured with strict separation of privilege and concerns:

### `USBGuardianService\` (Background Daemon)
The core security engine that runs silently with System privileges.

| File | Size | Lines | Purpose |
|------|------|-------|---------|
| `ConfigManager.cs` | 4.4 KB | ~120 | Handles parsing, saving, and querying `config.json` containing the Allowed/Blocked hardware rules and BCrypt password verifier. |
| `PipeServer.cs` | 5.7 KB | ~135 | Hosted IPC Named Pipe Server. Listens securely for authenticated lock/unlock commands from the UI and manages background threading. |
| `UsbPolicyManager.cs` | 8.4 KB | ~220 | The core logic engine. Handles precise native `pnputil` execution, Windows WMI `Win32_PnPEntity` queries, and port blocking logic. |
| `Worker.cs` | 1.1 KB | ~35 | The detached `.NET 8` BackgroundService loop that ensures the service remains active and pipes stay open. |
| `Program.cs` | 0.9 KB | ~30 | Dependency Injection setup initializing the service layer and logger hooks. |

### `USBGuardianControl\` (Admin Panel)
The visual dashboard for authenticated administrators to monitor port statuses and issue overriding commands.

| File | Size | Lines | Purpose |
|------|------|-------|---------|
| `MainWindow.xaml` | 8.7 KB | ~125 | The modern WPF markup view designing the Control Dashboard, password login, and dynamic hardware toggles. |
| `MainWindow.xaml.cs` | 6.9 KB | ~190 | The WPF code-behind logic. Pings the daemon via the Named Pipe client adapter, serializes WMI lists to JSON, and reacts to UI callbacks instantly. |
| `App.xaml` / `.cs` | 0.4 KB | ~20 | C# application bootstrapping logic. |

### `.\` (Root Admin Scripts)
Batch scripts designed to simplify execution, compilation, and management.

| File | Size | Lines | Purpose |
|------|------|-------|---------|
| `Install-Background-Service.bat` | 1.1 KB | ~25 | Uses `sc.exe` to deploy and initialize the .NET 8 Worker Service silently as an auto-starting global security daemon. |
| `Update-Service.bat` | 0.4 KB | ~10 | Safely halts the running Windows daemon, rebuilds the C# `.sln` and copies fresh `.exe` binaries before spinning it back up. |
| `Start-Control.bat` | 0.1 KB | ~3 | Fast track script to boot up the Admin UI Panel natively. |
| `Start-Service.bat` | 0.3 KB | ~10 | Fast track script to initialize the internal binary if bypassing the Installer pipeline. |
| `ChangeManagement.md`| 3.0 KB | 43 | The phase-by-phase chronological ledger outlining development pivots, bug fixes, and patch notes. |

## Complete Deployment Guide

**Step 1. Compilation**
Ensure your machine is equipped with the **.NET 8 SDK**. Open the command prompt in the `USB Blocker` root directory and type:
```cmd
dotnet build "USB Blocker.sln" -c Release
```
This builds both the Background Service and the Admin UI cleanly. Alternatively, you can run the `Update-Service.bat` script which will manage the pipeline for you.

**Step 2. Daemon Configuration**
Before you install the service, define your admin password. The defaults are hashed inside `C:\ProgramData\USBGuardian\config.json`. If you wish to change the default hash, generate a new BCrypt string and modify the configuration payload.
_The built-in default password string is `admin`._

**Step 3. Installing the Windows Background Policy Engine**
Right-click `Install-Background-Service.bat` and select **Run as Administrator**.
This process performs the following key steps automatically:
- Formally registers the executable under `services.msc` as `USBGuardianService`.
- Sets the `Start=Auto` directive so Port blocking is heavily enforced immediately traversing native Windows Boot loops.
- Sets the `obj=LocalSystem` directive, creating extreme tamper resistance guaranteeing local users cannot terminate the daemon via Task Manager.

**Step 4. Final Verification**
The system is now actively protecting the machine. Double-click the `Start-Control.bat` file to load the UI panel, login with the default test credentials (`admin`), and verify you can view and aggressively block the USB Root Hubs listed in your local physical topology.
