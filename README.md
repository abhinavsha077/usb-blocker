# USB Guardian Endpoint Security — Centralized Network Edition

USB Guardian is a robust, system-level security application designed to explicitly manage and lock down physical USB ports across a local area network (LAN). It prevents unauthorized data exfiltration via USB Mass Storage, Phones, and MTP devices by electronically disabling Root Hub ports at the driver level — and also force-ejects any devices that are already connected when a lock command is issued.

The latest version transitions from a standalone single-PC tool into a **Centralized Network Architecture**, allowing one Admin PC to manage the USB ports of any number of connected Endpoint PCs (Agents).

---

## Architecture

The system is split into three main components:

1. **USBGuardianServer (Central Hub)**
   - An `ASP.NET Core 8 Web API` application running as a Windows Service (`LocalSystem`) on the Admin PC.
   - Listens on `http://0.0.0.0:5050`.
   - Manages a registry of connected Agents using SignalR (WebSockets).
   - Stores the central admin hashing password (`server_config.json`) and granular per-machine USB policies (`server_policies.json`) in `C:\ProgramData\USBGuardian\`.
   - Exposes REST API endpoints for the Admin UI to issue lock/unlock/allow/block commands.

2. **USBGuardianAgent (Endpoint Daemon)**
   - A detached `.NET 8 Worker Service` running natively as `LocalSystem` on every endpoint PC you want to control.
   - Connects to the Server via SignalR WebSocket.
   - Loops every 5 seconds continuously enforcing security rules sent by the server. 
   - Uses WMI to scan physical hubs and connected devices, instantly pushing live visual updates back to the Server.
   - Stores Server IP mapping in `C:\ProgramData\USBGuardian\agent_config.json`.

3. **USBGuardianControl (Admin Dashboard)**
   - A secure `.NET 8 WPF` application used by the administrator exactly like a router configuration page.
   - Connects to the Server via HTTP using a BCrypt master password.
   - Displays a live-updating left panel of all connected machines (Online/Offline, Locked/Open status).
   - Selecting a machine loads its specific USB Root Hubs and connected devices in the right panel.

---

## Technical Mechanics

### Native Hub-Level Blocking
Unlike older, unreliable methods that simply disable the `USBSTOR` registry key, USB Guardian targets the physical **USB Root Hub** network on the motherboard. It uses WMI (`Win32_PnPEntity`) to locate physical hubs and executes native Windows `pnputil.exe` commands to cut the data/power pathways structurally.

### Force-Eject on Lock (Two-Layer Security)
When the system is locked, USB Guardian enforces a **two-layer** shutdown:
- **Layer 1** — Disables all USB Root Hubs (blocks new connections).
- **Layer 2** — Scans for and individually disables any `USBSTOR` (flash drives, HDDs) and MTP (phones, cameras) devices that are **already connected and mounted**, preventing mid-session data exfiltration.

When unlocked, both layers are reversed safely — hubs are re-enabled, and any previously force-ejected devices are restored. Operations only fire on state transformations to prevent Windows UI/USB-sound reconnect loops.

### Master Overrides
- **Unlock Indefinitely**: Global ALLOW rule for the selected machine.
- **Lock System**: Global BLOCK rule. Immediately force-ejects connected devices.
- **Timed Unlock**: Temporarily unlocks for N minutes, then auto-relocks the machine.
- **Granular Toggles**: Pick an individual USB Hub on a machine and explicitly allow or block it.
- **Global Lock All / Unlock All**: Sends an instant network command to lock/unlock every connected machine simultaneously.

---

## Complete Deployment Guide

### Step 1: Install the Server (Admin PC)
1. Run `Install-Server.bat` as Administrator.
2. The script compiles `USBGuardianServer`, opens Windows Firewall port `5050`, and registers it as an auto-starting Windows Service running as LocalSystem.
3. The Server is now live on port 5050.

### Step 2: Install Agents (Endpoint PCs)
1. Copy the project folder (or just `Dist\Agent`, `Install-Agent.bat`, and `dotnet-sdk-8.0.418-win-x64.exe`) to the target PC.
2. Run `Install-Agent.bat` as Administrator.
3. The installer will automatically silently install the .NET 8 Runtime if missing.
4. When prompted, type the **IP Address of your Admin PC** (e.g., `192.168.1.101`).
5. The Agent registers as a hidden service and immediately connects back to your Admin PC.

### Step 3: Use the Dashboard
1. On your Admin PC, double-click `Start-Control.bat` (runs the UI).
2. Ensure the Server URL is `http://192.168.1.101:5050` (or `localhost`).
3. Enter the default password (`admin`).
4. You will see every Agent you installed appear in the left-hand column.
5. Click on an Agent to view its physical USB ports and connected devices in real-time, and control them.

### Changing the Admin Password
1. Enter your current password in the main window and click **Connect**.
2. Click **⚙ Change Password** in the top-right.
3. Enter the new password. The new BCrypt hash is saved to the server immediately.

### Updating / Recompiling
On your development machine, right-click `Update-All.bat` -> Run as Administrator. It will cleanly stop both the Server and Agent services, thoroughly compile all 3 projects (`Server`, `Agent`, `Control UI`) to the `Dist` folder, and start the services back up.

---

## File Structure

### `USBGuardianServer\` (Central Hub)
| File | Lines | Purpose |
|------|-------|---------|
| `Program.cs` | ~80 | Configures ASP.NET Core DI, CORS, SignalR, and maps REST endpoints. |
| `AgentHub.cs` | ~60 | SignalR WebSocket Hub tracking connected agents, mapping IPs/Names, and broadcasting commands. |
| `AgentController.cs` | ~60 | REST API endpoints for the Admin Dashboard (handles `/api/agents/...`). |
| `PolicyStore.cs` | ~60 | JSON persistence engine for `server_policies.json` keeping granular allowed/blocked lists per agent. |
| `ServerConfigManager.cs` | ~40 | BCrypt admin password manager generating and validating `server_config.json`. |

### `USBGuardianAgent\` (Endpoint Daemon)
| File | Lines | Purpose |
|------|-------|---------|
| `UsbPolicyManager.cs` | ~230 | Core Execution Engine. Executes physical WMI `pnputil` device blocks, and tracks `ForceEject`/`ForceEnable` state transitions. |
| `AgentSignalRClient.cs` | ~130 | Persistent WebSocket client pushing live device lists to the Server and pulling Lock/Unlock commands. |
| `AgentConfigManager.cs` | ~40 | Generates and reads `agent_config.json` containing the Server URL and Machine ID. |
| `Worker.cs` | ~40 | `.NET 8` BackgroundService loop. Instructs `UsbPolicyManager` and `SignalRClient` to execute and push stats every 5 seconds. |
| `Program.cs` | ~30 | CLI Entry Point. Dependency injection bootstrapping registering the process as a Windows Service. |

### `USBGuardianControl\` (Admin Dashboard)
| File | Lines | Purpose |
|------|-------|---------|
| `MainWindow.xaml` | ~330 | WPF XAML UI containing dual-template visual lists for Hubs (⬡) vs Connected Devices (🔌) and Left-Panel Agent List. |
| `MainWindow.xaml.cs` | ~300 | UI Code-Behind. Safely tracks async UI threads, handles Connect/Refresh actions, and binds data dynamically to selected agents. |
| `ServerClient.cs` | ~100 | Wrapped HTTP client mapping UI button clicks to actual Server REST endpoints with embedded auth tokens. |
| `ChangePasswordWindow.xaml` | ~50 | Popup UI for changing the master admin password. |
| `ChangePasswordWindow.xaml.cs` | ~70 | Password logic and form validation for the popup. |
| `UsbDeviceViewModel.cs` | ~50 | Live UI data model computing state colors and `IsAllowed` policy tags. |

### Root Management Scripts (`.\`)
| File | Lines | Purpose |
|------|-------|---------|
| `Install-Server.bat` | ~40 | Compiles Server to `Dist\Server`, opens Firewall **Port 5050**, and registers the LocalSystem service. |
| `Install-Agent.bat` | ~60 | Prompts globally for Server IP, handles silent `.NET 8` runtimes, writes config, and starts the Agent daemon. |
| `Update-All.bat` | ~40 | A safe, 1-click update script that halts all services, recompiles 100% of the project, and restarts. |
| `Start-Control.bat` | ~10 | Spawns the WPF Dashboard with Administrative execution powers safely. |
