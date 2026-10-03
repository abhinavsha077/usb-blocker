# USB Guardian {{VERSION}} Acceptance Test Candidate

This bundle contains self-contained x64 Server, Endpoint, and Control MSIs. A test build may be unsigned; it is not a production-approved release. Validate the exact package hashes and complete TEST-CHECKLIST.md before rollout. Production builds require Build-Installers.ps1 -Production -CertificateThumbprint and successful Authenticode verification.

## Install and configure the Server

On the management computer, open PowerShell as Administrator in the extracted bundle:

```powershell
.\Tools\Install-Server-Test.ps1
.\Tools\Install-Control-Test.ps1
```

Connect the dashboard locally to http://127.0.0.1:5050 with the initial password admin and rotate it immediately. Configure a trusted HTTPS certificate and Security:RequireHttps=true before sending credentials across the network. See Documentation\EndpointEnrollment.md and Documentation\appsettings.Secure.example.json; replace the example certificate settings for your deployment. The Server MSI bootstrap secret is for explicit legacy migration; do not use it for new endpoints.

If Server startup fails, inspect %TEMP%\usbguardian-server-diagnostics.log and %TEMP%\usbguardian-server-install.log. Review logs for sensitive information before sharing them.

## Enroll and install each endpoint

On the administrator computer, issue one protected configuration per endpoint. MachineId must match that endpoint's computer name, or the explicit identity chosen for it:

```powershell
.\Tools\Provision-Endpoint.ps1 -ServerUrl https://guardian.example:5443 `
  -MachineId ENDPOINT-PC -OutputPath C:\SecureEnrollment\ENDPOINT-PC.json
```

This prompts securely for the administrator password. Transfer the protected configuration to its endpoint through your approved management channel. Keep it out of tickets, shared folders and chat. Issuing again rotates the token and revokes its old sessions.

On that endpoint, use an elevated PowerShell:

```powershell
.\Tools\Install-Endpoint-Test.ps1 -EndpointConfiguration C:\SecureEnrollment\ENDPOINT-PC.json
```

For a deliberately isolated HTTP compatibility test, both provision with -AllowHttp and pass -AllowHttp to the Endpoint helper. HTTP is not the production transport. Existing agent_config.json files take precedence over MSI registry settings; follow the migration guide instead of deleting them blindly.

Endpoint enforcement requires Windows build 19041 or newer. Use a supported, patched Windows edition. No separate .NET runtime installation is needed.

## Verify the complete product

Install the Control MSI on the administrator workstation and connect to the trusted HTTPS origin. Verify the exact endpoint identity, Online state, policy revision and fresh enforcement receipt. A Running service or healthy Server response alone does not prove hardware enforcement.

Complete TEST-CHECKLIST.md using approved USBSTOR/UASP/MTP hardware. Normal enforcement covers removable storage and portable devices; hubs, controllers and ordinary keyboards/mice are protected. This product does not promise blocking every USB class or preventing all first-arrival access.

## Removal and recovery

Endpoint uninstall invokes managed-device recovery. Failed IDs remain in C:\ProgramData\USBGuardian\agent_managed_state.json. Inspect recovery results and retain the Agent executable for an elevated --restore-managed-devices retry before broad removal. Include disconnected devices, failed enables, upgrade, repair and rollback in the acceptance run.
