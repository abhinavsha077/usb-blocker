# USB Guardian Troubleshooting

## Dashboard Cannot Connect

1. Confirm `USB Guardian Server` is running in `services.msc`.
2. From the dashboard PC, run:

   ```powershell
   Test-NetConnection -ComputerName SERVER-IP -Port 5050
   Invoke-RestMethod http://SERVER-IP:5050/health
   ```

3. Confirm the dashboard URL includes `http://` or `https://`.
4. Verify the admin password. A wrong password returns HTTP 401.
5. Confirm the Windows Firewall rule and any network firewall allow the
   configured `Http:Port`.

`TcpTestSucceeded: False` or a failed `/health` request means there is no
reachable Server listener; changing the dashboard password or restarting only
the Agent cannot repair that condition. On the Server PC, confirm this command
returns a listening socket:

```powershell
Get-NetTCPConnection -State Listen -LocalPort 5050
```

The source installer now waits for `/health` after starting the service and
fails with a startup-specific message if the process never opens the port.

The dashboard now treats non-success API responses as errors. If it says a
command failed, use the displayed server response instead of assuming the
policy changed.

## Remote Changes Are Rejected With The Default Password

This is expected. All remote admin API access and first-password rotation are disabled
while the password is `admin`. Open the dashboard on the Server machine, change
it to an 8-128 character password over loopback, and reconnect.

## Agent Does Not Appear

1. Confirm `USB Guardian Agent` is running as LocalSystem.
2. Inspect:

   ```text
   C:\ProgramData\USBGuardian\agent_config.json
   ```

3. Confirm `ServerUrl` is reachable from the endpoint.
4. Confirm `AgentToken` matches:

   ```text
   C:\ProgramData\USBGuardian\agent_token.txt
   ```

5. Check Windows Event Viewer for registration or SignalR errors.

The Agent retries a failed initial connection and reconnects after later
failures. Restarting the service is not normally required after a temporary
network interruption.

If the Server reports that registrations are disabled, set the same non-empty
`AgentSecret`/Agent token on both roles. An empty Server secret fails closed;
`Security:AllowUnauthenticatedAgents=true` is intended only for isolated
development.

## Removable Device Is Not Blocked

1. Confirm the endpoint is `LOCKED`.
2. Verify the item appears as a managed device, not a hub/controller inventory
   row.
3. Check whether its exact ID or model token is allowlisted.
4. Run the Agent as LocalSystem; an unelevated process cannot reliably use
   `pnputil /disable-device`.
5. Check Agent logs for the exact `pnputil` exit code and error.
6. Confirm the device appears under `Win32_PnPEntity` as `USBSTOR` or matches the
   portable/MTP query.

USB Guardian intentionally does not disable hubs or host controllers.

## Device Remains Disabled After Allow Or Unlock

Check the Agent log for `pnputil /enable-device` failure. Some hardware requires
physical reinsertion after Windows re-enables the device node.

Unlock permits all removable/MTP devices without erasing saved rules. Relocking
will reapply the allow/block lists.

If recovery is urgent, run `Uninstall-All.bat` as Administrator. Current builds
restore only IDs in `agent_managed_state.json`; a broad hub recovery runs only
when the legacy v1 service is detected. Failed IDs remain in the state file.

## Timed Unlock Does Not Relock

The server policy file should contain both:

```json
"IsUnlocked": true,
"UnlockExpiry": "UTC timestamp"
```

The Agent also caches that expiry locally. Check system time synchronization on
both machines. A reconnect must not remove or extend the expiry.

## Learn Mode Stops During A Network Outage

This is expected fail-closed behavior. Learn mode permits devices only while the
Agent has an authenticated server connection and the configured expiry is still
valid. It resumes after reconnect only if the server-side enrollment window has
not expired.

## Notifier Does Not Appear

1. Confirm the current user logged on after Agent installation.
2. Check this registry value:

   ```text
   HKLM\Software\Microsoft\Windows\CurrentVersion\Run\USBGuardianNotifier
   ```

3. Confirm `Dist\Notifier\USBGuardianNotifier.exe` exists.
4. Check `C:\ProgramData\USBGuardian\alerts` for pending JSON files.
5. Launch the Notifier manually in the user's session to isolate startup-policy
   restrictions.

The Agent service runs in session 0 and cannot display WPF UI directly.

## Wi-Fi Blocked The Agent Connection

Wi-Fi policy is enforced locally, so the server cannot reverse it over the
disabled adapter. Use a wired connection or local administrator access, then
run:

```powershell
Get-PnpDevice -Class Net |
  Where-Object FriendlyName -Match 'Wireless|Wi-Fi|802.11' |
  Enable-PnpDevice -Confirm:$false
```

After connectivity returns, allow the Wi-Fi sector or use a policy appropriate
for that endpoint.

## Where To Look

- Windows Event Viewer: Server and Agent service logs
- `C:\ProgramData\USBGuardian\server_policies.json`: persisted policy
- `C:\ProgramData\USBGuardian\server_inventory.json`: persisted endpoint identity and last-seen metadata
- `C:\ProgramData\USBGuardian\agent_policy_cache.json`: endpoint offline policy
- `C:\ProgramData\USBGuardian\agent_managed_state.json`: removable, sector, and BadUSB devices disabled by the Agent
- `C:\ProgramData\USBGuardian\agent_event_queue.json`: durable events awaiting Server acknowledgement
- `C:\ProgramData\USBGuardian\events`: per-machine JSONL history
- `C:\ProgramData\USBGuardian\usbguardian_cef.log`: optional CEF output
- Dashboard History window: operator-facing event view
