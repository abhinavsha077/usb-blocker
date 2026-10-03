# Endpoint console

The Control application uses a permanent navigation rail and a full-height
endpoint browser. Start the published app with `Start-Control.bat`.

1. Expand **Server connection**, enter your server URL and admin password, and
   select **Connect**. Pressing Enter in either connection field also connects.
   The form collapses after a successful connection; expand it to reconnect or
   change the password. The active server address remains visible in the header.
2. Use the endpoint list to select a machine. Search by name, machine identity,
   IP address or group, and filter by online, offline or attention status.
   Filtering out the selected endpoint clears the command target.
3. Select **Devices** in the navigation rail for removable device inventory with
   separate **State** and **Policy** columns. Allow and Block save policy changes; verify the
   endpoint's report before treating a change as applied. Hubs and controllers
   are available through **Show hubs and controllers**, for display only.
4. **Policies** contains lock/unlock, temporary access, learn mode, device
   class controls and BadUSB protection. Toggle button labels state the next
   action. Temporary access accepts 1–10080 minutes.
5. **Manage** contains group assignment, group policies, agent reload and
   power actions. Power actions require an online endpoint and confirmation.
   **Fleet actions** at the bottom of the endpoint list affect every known machine.
6. **Event history** opens activity for the selected endpoint, including offline
   machines. History loading errors are distinct from an empty event log.

The summary strip in **Devices** covers the entire fleet, even when the endpoint
list is filtered. **Attention** includes enforcement errors and pending or stale
verification on online endpoints. Offline machines have their own count. Status
uses words as well as colors; a locked policy uses blue rather than an alarm color.

The console refreshes every five seconds after connecting. Press **F5** or select
**Refresh** to request an immediate update. During a policy command, endpoint
switching and other commands are temporarily disabled. A lost server connection
clears live device inventory and counts, marks cached inventory as unverified,
disables commands and expands the reconnect form.

Smaller windows hide the introductory heading to leave more room for inventory.
Policy and administration sections scroll vertically. All windows share the
same typography, colors, spacing and keyboard focus styles from `Theme.xaml`.
Inputs, dropdowns, checkboxes, expanders and scrollbars use custom WPF templates.

Build and publish the UI without restarting services:

```powershell
dotnet publish USBGuardianControl/USBGuardianControl.csproj -c Release -o Dist/Control
```

See `tools/ControlGuiChecks/README.md` for simulated-server GUI checks and rendered
previews. Live endpoint behavior still requires the hardware acceptance gate.
