# MSI And GPO Deployment

## Package Roles

USB Guardian uses three x64 MSI packages:

| Package | Installs |
|---|---|
| `USBGuardian-Server-<version>-x64.msi` | Server executable, LocalSystem service, recovery actions, and TCP 5050 firewall rule |
| `USBGuardian-Endpoint-<version>-x64.msi` | Agent LocalSystem service, user-session Notifier, and notifier startup registration |
| `USBGuardian-Control-<version>-x64.msi` | Control dashboard and Start menu shortcut |

The packages contain self-contained .NET 8 executables. Target machines do not
need a separate .NET runtime installation.

## Build

Build on Windows 10/11 or Windows Server with the .NET 8 SDK:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\USBGuardianInstaller\Build-Installers.ps1 -Version 1.1.0
```

Outputs and `SHA256SUMS.txt` are written under
`USBGuardianInstaller\output`. WiX 4.0.6 is restored through the pinned
SDK-style projects. Generated payload and output directories are replaced on
each run so packages from older versions cannot leak into the release set.

For production Authenticode signing:

```powershell
.\USBGuardianInstaller\Build-Installers.ps1 `
  -Version 1.1.0 `
  -CertificateThumbprint 'YOUR_CODESIGNING_CERTIFICATE_THUMBPRINT'
```

The script signs payload executables before packaging and signs each MSI after
linking. It fails if `signtool.exe` or the requested certificate is unavailable.

## Initial Silent Installation

Generate one high-entropy deployment token and protect it as a credential:

```powershell
$token = [guid]::NewGuid().ToString('N')
```

Install the Server:

```powershell
msiexec /i USBGuardian-Server-1.1.0-x64.msi `
  AGENTTOKEN="$token" `
  /qn /norestart /l*v C:\Windows\Temp\usbguardian-server.log
```

Enroll each endpoint separately following [EndpointEnrollment.md](EndpointEnrollment.md).
The server MSI token is a legacy bootstrap value and is not an endpoint credential.
Install an Endpoint with the credential issued for its MachineId:

```powershell
msiexec /i USBGuardian-Endpoint-1.1.0-x64.msi `
  SERVERURL="https://usb-guardian.example.internal:5443" `
  MACHINEID="OFFICE-PC-01" AGENTTOKEN="$endpointToken" `
  /qn /norestart /l*v C:\Windows\Temp\usbguardian-endpoint.log
```

Install the dashboard:

```powershell
msiexec /i USBGuardian-Control-1.1.0-x64.msi `
  /qn /norestart /l*v C:\Windows\Temp\usbguardian-control.log
```

`AGENTTOKEN` is marked both hidden and secure in MSI. The Server stores it under
`HKLM\SOFTWARE\USBGuardian\Server`; the Endpoint stores it under
`HKLM\SOFTWARE\USBGuardian\Agent`. Both registry keys receive a protected ACL
for SYSTEM and Administrators. Existing settings are carried through repairs and
major upgrades, and an explicitly supplied property can rotate them.

The source-based batch installer writes `agent_config.json`. When that JSON file
exists it remains authoritative; remove or migrate it before converting an
existing batch-installed endpoint to MSI-managed registry configuration.

## Group Policy Software Installation

1. Place the signed Endpoint MSI on a UNC share readable by Domain Computers and
   writable only by deployment administrators.
2. Provision each machine identity. Deliver its protected configuration or a
   per-machine MST setting `SERVERURL`, `MACHINEID`, and that machine's `AGENTTOKEN`.
3. Protect the MST with the same share permissions because it contains the
   endpoint credential. Do not reuse a credential or transform across machines.
4. Under **Computer Configuration > Policies > Software Settings > Software
   installation**, assign the MSI and its transform.
5. Enable the policy that waits for the network during computer startup.
6. Pilot against a dedicated OU before broad assignment.

Use computer assignment, not user assignment: the Agent is a LocalSystem service
and device enforcement is machine-wide.

## Upgrade And Removal

Every role has a stable UpgradeCode and automatically rejects downgrades. For a
major upgrade, deploy the higher three-part version. Existing endpoint/server
settings are discovered before the previous product is removed and are supplied
to the replacement package.

To rotate an endpoint credential, reissue it through the administrator enrollment
API, update that endpoint's JSON or registry settings, and restart its Agent. The
new token must remain bound to the same MachineId. Keep JSON/registry precedence in
mind: updating an MSI property alone does not replace an existing authoritative
`agent_config.json`. Server bootstrap secrets are independent of enrolled tokens.

Endpoint uninstall first stops the Agent and runs
`USBGuardianAgent.exe --restore-managed-devices`. This restores only hardware
recorded as disabled by USB Guardian. The custom action does not abort uninstall
if Windows refuses a device operation; unresolved IDs remain in
`agent_managed_state.json` for manual recovery. MSI then removes its services,
files, startup entries, firewall rules, shortcuts, and protected registry
configuration. Other ProgramData policies, events, and endpoint cache files are
also retained for audit and recovery.

## Qualification Gate

Before production assignment, execute this matrix on representative Windows
hardware:

1. Fresh install, service startup, dashboard connection, and notifier logon.
2. Repair without properties and verify configuration is preserved.
3. Major upgrade with and without credential rotation.
4. Downgrade rejection.
5. Endpoint and Server uninstall, including tracked-device restoration and
   service/firewall/startup cleanup.
6. Reinstall after uninstall.
7. The USB hardware acceptance gate in the root README.
