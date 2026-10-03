# USB Guardian User Guide

## First-Time Setup

The Server must be reachable before the dashboard or endpoint Agents can
connect. On the Server computer, verify:

```powershell
Invoke-RestMethod http://127.0.0.1:5050/health
```

The response must contain `status: healthy`.

For the first dashboard connection:

1. Open the dashboard on the Server computer.
2. Enter `http://127.0.0.1:5050`.
3. Enter the default password `admin` and click **Connect**.
4. Select **Change Password** and choose an 8–128 character password.
5. Use the new password for all later local and remote connections.

Remote administration is deliberately rejected while `admin` remains the
default password. An Agent uses its separate agent token; it does not use the
dashboard password.

## Dashboard Connection

Enter the server URL and admin password, then click **Connect**. A successful
connection loads all managed machines and starts a five-second
refresh. Authentication or API failures are shown in the status bar; failed
commands are not presented as successful.

The dashboard does not enforce policy itself. The Server and endpoint Agents
continue working when the dashboard is closed.

Use the correct URL for where the dashboard is running:

| Dashboard location | Example URL |
|---|---|
| On the Server computer | `http://127.0.0.1:5050` |
| Another LAN computer | `http://NORMOS:5050` or `http://SERVER-IP:5050` |

If the dashboard says it cannot reach the Server, the password has not yet been
checked because no HTTP response was received. Verify the Server computer with:

```powershell
Get-Service "USB Guardian Server"
Get-NetTCPConnection -State Listen -LocalPort 5050
Invoke-RestMethod http://127.0.0.1:5050/health
```

If those commands work locally but the remote dashboard still fails, run
`Test-NetConnection -ComputerName SERVER-NAME -Port 5050` from the dashboard
computer and check DNS and firewall rules.

## Confirming Agent Connection

After installing an endpoint, it should appear as **Online** within approximately
ten seconds. If the Server is healthy but no machine appears, check the endpoint:

```powershell
Get-Service "USB Guardian Agent"
Get-Content C:\ProgramData\USBGuardian\agent_config.json
```

The configuration must be valid, non-empty JSON containing:

- `ServerUrl`: the Server DNS name or IP address
- `MachineId`: the endpoint identity
- `AgentToken`: the credential enrolled for this endpoint's exact MachineId

Do not configure an endpoint with `localhost` unless the Server is installed on
that same endpoint. If the file is empty or malformed, rerun `Install-Agent.bat`
as Administrator and supply the correct Server URL and token.

## Recommended Daily Workflow

1. Connect the dashboard and confirm the Server reports connected.
2. Check that the intended endpoint is **Online**.
3. Review its current policy and connected devices.
4. Use **Allow** for approved devices before locking when appropriate.
5. Apply **Lock**, timed unlock, learn mode, or sector changes.
6. Confirm the endpoint reports the new state and review **History**.

Do not use **Lock All**, Wi-Fi blocking, reboot, or shutdown until the affected
machine scope and recovery path have been confirmed.

## Machine Status

Select a machine in the left panel. The header shows:

- Online state and IP address
- Removable-device sector state: `LOCKED` or `OPEN`
- Assigned group
- Learn-mode state
- Timed-unlock end time, when applicable
- Last-seen time when the endpoint is offline

`LOCK POLICY VERIFIED` means the endpoint reported a recent successful enforcement
scan for the saved revision. `POLICY PENDING`, `ENFORCEMENT STALE`, and
`ENFORCEMENT ERROR` require attention. Saved lock/open flags describe intent;
device rows show observed enabled, disabled/faulted, or unknown state separately.
This product targets removable storage and portable devices while preserving
shared USB hubs/controllers and normal input devices.

Offline machines remain in the list. Their policy, group, and history can still
be managed, and changes apply after reconnection. Live device rows and the
Reload, Reboot, and Shutdown commands are unavailable while an endpoint is
offline.

## Hardware And Managed Devices

Hub and host-controller rows are inventory only. Their hardware status is
displayed, but they have no policy buttons because normal enforcement never
disables them.

Removable storage, phones, cameras, and other MTP/portable devices appear as
managed device rows. Those rows provide:

- **Allow**: keep this exact device usable while the removable sector is locked
- **Block**: deny this device while the removable sector is locked

The policy API also accepts VID/PID or VEN/PROD model tokens for model-wide
authorization. The current dashboard sends exact instance IDs.

## Lock, Unlock, And Timed Unlock

- **Lock** activates removable-device policy on the selected endpoint.
- **Unlock** permits all removable and MTP devices indefinitely.
- Timed presets or a custom duration permit devices until the displayed UTC
  expiry is reached.

Unlocking does not delete the allowlist or blocklist. Saved device rules become
active again when the endpoint relocks.

**Lock All** and **Unlock All** update every known machine policy. Online agents
receive the change immediately; offline agents receive it when they reconnect.

## Learn Mode

Learn mode temporarily permits removable/MTP devices and records each connected
device in the endpoint allowlist. It defaults to eight hours and cannot exceed
48 hours.

Learn mode is intentionally fail-closed. It stops permitting new devices when:

- Its expiry is reached
- The endpoint loses its authenticated server connection
- An administrator turns it off

Use learn mode only during a controlled enrollment window, then review the event
history and disable it.

## Groups

Assign a selected endpoint to a named group. Group Lock and Group Unlock update
all known policies in that group, including machines that are currently offline.

## Optional Sectors

The selected endpoint can independently block:

- Optical drives
- Bluetooth devices
- Wi-Fi adapters

When a sector is allowed again, USB Guardian restores only devices that it
previously disabled. Hardware disabled manually or by another management tool is
left unchanged.

Wi-Fi blocking can disconnect the endpoint from the server. Use it only when a
wired management path or local recovery procedure is available.

## BadUSB

The agent records the keyboards present at service startup as its trusted
baseline. Later keyboard arrivals are reported. With auto-block enabled, a new
keyboard that is neither baseline-trusted nor allowlisted is disabled directly.
Disconnecting and reinserting an untrusted keyboard triggers assessment again.
Turning BadUSB auto-block off, or allowlisting that keyboard, restores it only
when USB Guardian previously performed the disable action.

Test this feature with approved spare input devices before production rollout.

## Alerts And History

When a removable device is successfully disabled, the Agent writes an atomic
alert signal under `C:\ProgramData\USBGuardian\alerts`. The Notifier running in
the interactive user session displays the alert.

**History** shows endpoint insertions, policy changes, learn captures, BadUSB
events, and remote management actions. Server-side event files are stored under
`C:\ProgramData\USBGuardian\events`. If the Server is unavailable, the Agent
retains up to 5,000 events in `agent_event_queue.json` and resends them with
stable IDs after reconnect, preventing duplicate history entries.

## Remote Management

Reload asks the Agent to enforce immediately and re-register. Reboot and
Shutdown show the user a 20-second Windows warning. These actions require the
endpoint to be online and always show a confirmation where appropriate.

## Password And Agent Token

Change the default admin password from a dashboard running on the Server machine
before remote administration. The first rotation is loopback-only. New passwords
must contain 8 to 128 characters and cannot be `admin`.

Each Agent must use its own enrolled credential. Issue or rotate it using
`Provision-Endpoint.ps1` and follow [EndpointEnrollment.md](EndpointEnrollment.md).
The server installer's protected `agent_token.txt` is a legacy bootstrap value,
accepted only for unbound machines during explicitly enabled shared-token migration.
After migration, disable legacy mode. A token belonging to another endpoint is rejected.

## Connection Message Reference

| Message or symptom | What it means | What to check |
|---|---|---|
| `Cannot reach the USB Guardian Server` | No process answered at the URL | Server service, port 5050 listener, URL, DNS, firewall |
| Server response timeout | A connection or response did not complete in eight seconds | Routing, firewall inspection, Server health/load |
| `Authentication failed` | The Server is reachable but rejected the password | Current password and local first-time rotation |
| Connected, zero machines | Dashboard-to-Server works but Agents are absent | Agent service, `agent_config.json`, Server URL, token |
| Machine shown as Offline | It registered previously but is not connected now | Endpoint power/network, Agent service, SignalR reachability |

Restart services only after checking their URL and configuration. Repeated
restarts do not repair a closed Server port, an empty Agent configuration, or a
token mismatch.
