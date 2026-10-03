# Endpoint enrollment and secure transport

Every endpoint now has a random credential bound to its exact MachineId. The server
stores hashes in `C:\ProgramData\USBGuardian\endpoint_credentials.json`. Back up this
protected file with the server policy/configuration files. A token from another PC
cannot register under an enrolled identity or replace its active session.

## New deployment

1. Install the Server and rotate `admin` locally using the dashboard.
2. Configure a trusted server certificate for the DNS name used by endpoints. Merge
   `USBGuardianServer/appsettings.Secure.example.json` into the installed Server's
   `appsettings.json`, replace the certificate subject, and restart the Server.
   The certificate and private key must be in `LocalMachine\My`, valid for server
   authentication, accessible to LocalSystem, and trusted on every client. HTTPS uses
   port 5443. Add a firewall rule for that port limited to your management network;
   remove the 5050 rule after all clients use HTTPS. `RequireHttps=true` rejects HTTP
   API requests and every SignalR transport before authentication.
3. Run this on a trusted administrator machine, separately for each endpoint:

   ```powershell
   .\Provision-Endpoint.ps1 -ServerUrl https://usb-guardian.example.internal:5443 `
     -MachineId OFFICE-PC-01 -OutputPath C:\SecureDeployment\OFFICE-PC-01.json
   ```

   The script prompts securely for the admin password, calls the authenticated
   enrollment API, and writes a protected JSON file. It does not print the token.
   Use `-AllowHttp` explicitly only for the retained HTTP LAN compatibility mode.
   An HTTPS enrollment sets `RequireHttps=true` in endpoint configuration.
4. Securely transfer the protected JSON to that endpoint's deployment staging
   folder. For JSON-based deployment, use an **elevated** PowerShell and the source
   or bundled `Write-AgentConfig.ps1` so the runtime file is protected at creation:

   ```powershell
   $endpoint = Get-Content C:\SecureDeployment\OFFICE-PC-01.json -Raw | ConvertFrom-Json
   $env:SERVER_URL = $endpoint.ServerUrl
   $env:MACHINE_ID = $endpoint.MachineId
   $env:AGENT_TOKEN = $endpoint.AgentToken
   $env:CONFIG_PATH = 'C:\ProgramData\USBGuardian\agent_config.json'
   .\Write-AgentConfig.ps1
   $env:AGENT_TOKEN = $null
   ```

   Runtime files must be owned by SYSTEM or Administrators, with only those
   principals able to modify them. The service refuses files owned by other users,
   links, or unreadable ownership journals; repair trusted files administratively
   instead of deleting recovery state. Delete extra deployment copies after
   confirming installation. Keep MachineId consistent; normally it is the PC name.
5. For MSI installation, read that endpoint's JSON in an elevated PowerShell window:

   ```powershell
   $endpoint = Get-Content C:\ProgramData\USBGuardian\agent_config.json -Raw | ConvertFrom-Json
     msiexec /i USBGuardian-Endpoint-1.1.4-x64.msi `
     SERVERURL="$($endpoint.ServerUrl)" MACHINEID="$($endpoint.MachineId)" `
     AGENTTOKEN="$($endpoint.AgentToken)" /qn /norestart
   ```

   MSI hides AGENTTOKEN in installer logs and protects registry configuration.
   Administrative command-line/process inspection can still see MSI properties;
   restrict deployment tooling access. JSON takes precedence over MSI registry
   configuration. Keep the protected JSON for RequireHttps enforcement, or migrate
   carefully to registry settings and retain an HTTPS SERVERURL.
6. Verify the endpoint is online and the dashboard reports a recent verified policy.
   A saved policy alone does not demonstrate successful hardware enforcement.

## Upgrade from shared tokens

The default now rejects shared-token registration. During a coordinated migration,
temporarily set `Security:AllowLegacySharedAgentToken=true` on the Server, keeping
the existing AgentSecret. This retains the old impersonation weakness for endpoints
that have not yet been enrolled. Enrolled identities always require their individual
token, even while compatibility mode is enabled. Never use compatibility mode as
the production completion criterion.

Provision one identity at a time, replace its protected JSON (or registry token),
restart its Agent, and verify reconnection. Reissuing a credential immediately
revokes the previous token and active session for that identity. Once every endpoint
is enrolled, set `AllowLegacySharedAgentToken=false` and
`AllowUnauthenticatedAgents=false`, restart the Server, and verify the full fleet.
Do not distribute one MST containing one credential to all PCs. Use per-machine
transforms or protected per-machine JSON delivery through your deployment system.

## Release acceptance

Run the README hardware gate on representative USBSTOR drives, UASP enclosures,
MTP phones/cameras, and composite devices, including arrival while locked, unlock,
allow/block rules, expiry, disconnected-server enforcement, reboot, and uninstall
restoration. Verify keyboard/mouse availability throughout. Sign executables/MSIs
and complete fresh-install/repair/upgrade/uninstall testing before broad rollout.
The provided secure profile is a configuration template; it does not install a
certificate or change services/firewall rules automatically.
