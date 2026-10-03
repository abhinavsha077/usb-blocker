# USB Guardian Deployment Guide

This guide covers a source-based installation using the supplied batch files.
For MSI/GPO deployment, see `MSI_GPO_DEPLOYMENT.md`.

## Deployment Order

Deploy the roles in this order:

1. Install and verify the Server.
2. Connect locally and replace the default admin password.
3. Enroll each endpoint identity with its own credential, then install its Agent;
   follow [EndpointEnrollment.md](EndpointEnrollment.md).
4. Install or launch the Control dashboard and confirm every endpoint is online.
5. Complete the hardware acceptance gate before applying a fleet-wide lock.

Do not install endpoints until the Server `/health` request succeeds. Restarting
an Agent cannot repair a Server that is not listening.

## Prerequisites

- Windows 10/11 or Windows Server with administrator access
- .NET 8 SDK when using the source-based batch installers
- TCP connectivity from endpoints and dashboard machines to the server
- A fixed server DNS name or IP address
- A maintenance window and local recovery access for hardware acceptance tests

Before installation, decide which address endpoints and remote dashboards will
use, for example:

```text
http://NORMOS:5050
http://192.168.1.20:5050
```

Use `localhost` only for a dashboard or Agent running on the Server computer
itself. On a separate endpoint, `localhost` refers to that endpoint—not the
USB Guardian Server.

The supplied scripts publish from source. For controlled enterprise rollout,
publish signed artifacts once and distribute those immutable outputs rather than
building independently on every endpoint.

The WiX packages are self-contained and do not require a separately installed
.NET runtime. Build and GPO instructions are in `MSI_GPO_DEPLOYMENT.md`.

## Server

Open Command Prompt as Administrator on the management computer and run:

```bat
Install-Server.bat
```

The installer:

1. Publishes `USBGuardianServer` to `Dist\Server`
2. Generates or reuses a shared agent token
3. Writes the token into the published server configuration
4. Restricts the token file to SYSTEM and Administrators
5. Opens inbound TCP 5050 in Windows Firewall
6. Creates or updates the `USB Guardian Server` LocalSystem service
7. Waits for `http://127.0.0.1:5050/health` and fails if the listener never opens

The token is stored at:

```text
C:\ProgramData\USBGuardian\agent_token.txt
```

Open the dashboard on the Server machine with the default password `admin`, then
change the password immediately. The first password change is accepted only from
loopback, and remote admin API access remains disabled while the default password
is active.

### Verify The Server

Run these commands on the Server computer after installation:

```powershell
Get-Service "USB Guardian Server"
Get-NetTCPConnection -State Listen -LocalPort 5050
Invoke-RestMethod http://127.0.0.1:5050/health
```

Expected results:

- Service status is `Running`.
- A listener exists on port 5050.
- `/health` returns `status: healthy`.

From another computer, verify network reachability:

```powershell
Test-NetConnection -ComputerName NORMOS -Port 5050
Invoke-RestMethod http://NORMOS:5050/health
```

If the local health request fails, inspect Windows Event Viewer before
reinstalling endpoints. If local health works but the remote request fails,
check DNS, the Windows Firewall rule, and intervening network firewalls.

The default endpoint is `http://0.0.0.0:5050`. Configure `Http:BindAddress`,
`Http:Port`, and the firewall rule together if a different endpoint is required.
For HTTPS-only operation, configure the certificate through the normal ASP.NET
Core/Kestrel mechanism, set `Https:Enabled=true`, and set `Http:Enabled=false`.

## Endpoint

After Server health verification, run `Install-Agent.bat` as Administrator on
each endpoint and provide:

- Full HTTP/HTTPS Server URL, or a bare IP/DNS name for default HTTP port 5050
- Individual endpoint credential issued by `Provision-Endpoint.ps1` for this PC

The script:

1. Verifies the .NET 8 SDK required to publish from source.
2. Calls the Server `/health` endpoint and stops if it is unreachable.
3. Publishes Agent and Notifier.
4. Writes `C:\ProgramData\USBGuardian\agent_config.json` atomically.
5. Reads the file back and rejects an empty, malformed, or incomplete config.
6. Creates or updates the `USB Guardian Agent` LocalSystem service.
7. Registers the Notifier under the machine-wide Windows `Run` key.

The Notifier starts at the next user logon. The Agent starts immediately and
retries both initial and later server connection failures.

### Verify An Endpoint

Run:

```powershell
Get-Service "USB Guardian Agent"
Get-Content C:\ProgramData\USBGuardian\agent_config.json
Test-NetConnection -ComputerName NORMOS -Port 5050
```

Confirm that:

- The Agent service is `Running` as LocalSystem.
- `ServerUrl` contains the real Server address, not endpoint-local `localhost`.
- `MachineId` is populated.
- `AgentToken` is non-empty and was enrolled for this exact `MachineId`.
- The endpoint appears as **Online** in the dashboard within approximately ten
  seconds.

Treat the token and configuration file as credentials. Do not paste their
contents into tickets or general-purpose logs.

## Dashboard

Publish or run `USBGuardianControl`, or use `Start-Control.bat` after
`Update-All.bat`.

For the first connection, run the dashboard on the Server computer and use:

```text
Server:   http://127.0.0.1:5050
Password: admin
```

Change the password to an 8–128 character value. After that rotation, remote
dashboards can connect with the Server DNS name or IP address:

```text
http://SERVER-IP:5050
```

The dashboard password is never saved. The last server URL is stored in the
current user's `%AppData%\USBGuardian\control_settings.json`.

Connection messages have distinct meanings:

| Dashboard message | Meaning | Action |
|---|---|---|
| `Cannot reach the USB Guardian Server` | No HTTP response was received | Verify service, listener, URL, DNS, and firewall |
| `did not respond within 8 seconds` | The request timed out | Check network routing/firewall and Server load |
| `Authentication failed` | The Server responded but rejected the password | Use the current password; rotate default `admin` locally first |
| Connected with no machines | Server works, but no Agent is registered | Verify Agent service, URL, config file, and token |

## Updating

Run `Update-All.bat` as Administrator on a development or combined installation.
It stops installed services and running Control/Notifier processes, publishes
Server, Agent, Control, and Notifier, refreshes the Notifier startup entry,
reapplies service recovery settings, and verifies installed services restart.

For a real fleet, stage versioned outputs and update endpoints through a signed
MSI/software-distribution system. `USBGuardianInstaller\Build-Installers.ps1`
builds separate Server, Endpoint, and Control packages with stable upgrade codes,
service recovery, protected configuration, and SHA-256 manifests.

After every Server update, repeat the local `/health` check. After every Agent
update, confirm the endpoint returns to **Online** before applying policy.

## Connection Recovery Checklist

When both the machine-name URL and `localhost` fail on the Server computer:

1. Run `Get-Service "USB Guardian Server"`.
2. Run `Get-NetTCPConnection -State Listen -LocalPort 5050`.
3. Run `Invoke-RestMethod http://127.0.0.1:5050/health`.
4. Check Windows Event Viewer for a Server startup exception.
5. Rerun `Install-Server.bat` from an elevated Command Prompt to publish and
   register the corrected Server build.

When Server health succeeds but the endpoint is missing:

1. Verify `USB Guardian Agent` is running.
2. Validate `agent_config.json` is non-empty JSON.
3. Confirm the configured URL is reachable from that endpoint.
4. Confirm the Agent token was enrolled for this endpoint's exact MachineId.
5. Restart the Agent only after correcting its configuration.

## Uninstalling

Run `Uninstall-All.bat` as Administrator on each affected machine. It stops the
services first, then asks the Agent to restore only device IDs recorded in
`agent_managed_state.json`. A legacy hub-wide recovery pass runs only when the
old v1 service is detected. It then removes the legacy, Agent, and Server services
plus the Notifier startup entry. Runtime policy, inventory, event, and any failed
recovery IDs under ProgramData are retained for audit and manual recovery.

Endpoint MSI uninstall stops the Agent and invokes the same ownership-based
device recovery before removing files. MSI uninstall also removes installed
services, startup entries, firewall rules, and MSI-owned registry credentials.
ProgramData policy, inventory, event history, and unresolved recovery IDs remain
available for audit or explicit cleanup.

## Production Gate

Do not approve deployment solely from a successful build. Complete the hardware
acceptance steps in the root README on each supported Windows/controller class.
Record Event Viewer logs, `pnputil` exit results, endpoint history, keyboard and
mouse continuity, and uninstall recovery.
