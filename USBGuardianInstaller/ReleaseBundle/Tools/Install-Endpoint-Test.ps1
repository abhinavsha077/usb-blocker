[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EndpointConfiguration,
    [switch]$AllowHttp
)
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Open PowerShell as Administrator and run this script again.'
}
if ([Environment]::OSVersion.Version.Build -lt 19041) {
    throw 'Endpoint device enforcement requires Windows build 19041 or newer.'
}
$endpoint = Get-Content -LiteralPath $EndpointConfiguration -Raw | ConvertFrom-Json
$ServerUrl = [string]$endpoint.ServerUrl
$uri = $null
if (-not [Uri]::TryCreate($ServerUrl, [UriKind]::Absolute, [ref]$uri) -or
    $uri.Scheme -notin 'http', 'https' -or $uri.UserInfo -or $uri.Query -or
    $uri.Fragment -or $uri.AbsolutePath -ne '/') {
    throw 'Endpoint configuration must contain a valid HTTP(S) server origin.'
}
if ($uri.Scheme -ne 'https' -and (-not $AllowHttp -or $endpoint.RequireHttps)) {
    throw 'Use HTTPS, or select -AllowHttp for an explicitly provisioned LAN compatibility configuration.'
}
$MachineId = [string]$endpoint.MachineId
$AgentToken = [string]$endpoint.AgentToken
if (-not $MachineId -or $MachineId.Length -gt 128 -or $MachineId -ne $MachineId.Trim() -or
    $MachineId -match '[\x00-\x1f\x7f/\\"]' -or $AgentToken -notmatch '^[A-Fa-f0-9]{64}$') {
    throw 'Use the protected configuration issued by Provision-Endpoint.ps1 for this endpoint.'
}
$ServerUrl = $uri.AbsoluteUri.TrimEnd('/')
$legacyConfig = 'C:\ProgramData\USBGuardian\agent_config.json'
if (Test-Path -LiteralPath $legacyConfig) {
    throw "Existing JSON configuration takes precedence over MSI settings: $legacyConfig. Migrate it using EndpointEnrollment.md before this clean MSI test."
}
$health = Invoke-RestMethod -Uri ($ServerUrl + '/health') -TimeoutSec 8
if ($health.status -ne 'healthy') { throw 'Server health response was not healthy.' }
$packages = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '..\Installers') -Filter 'USBGuardian-Endpoint-*-x64.msi' -File)
if ($packages.Count -ne 1) { throw "Expected exactly one Endpoint MSI; found $($packages.Count)." }
$logPath = Join-Path $env:TEMP 'usbguardian-endpoint-install.log'
$arguments = @('/i', "`"$($packages[0].FullName)`"",
    "SERVERURL=$ServerUrl", "`"MACHINEID=$MachineId`"", "AGENTTOKEN=$AgentToken",
    '/qn', '/norestart', '/l*v', "`"$logPath`"")
$process = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\msiexec.exe') -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
if ($process.ExitCode -notin 0, 3010) {
    throw "Endpoint installation failed with exit code $($process.ExitCode). Log: $logPath"
}
$service = Get-Service -Name 'USB Guardian Agent' -ErrorAction Stop
if ($service.Status -ne 'Running') { throw "Agent service is $($service.Status). Log: $logPath" }
Write-Host 'USB Guardian Endpoint installed. Confirm its identity, Online state and enforcement receipt in the dashboard.' -ForegroundColor Green
if ($process.ExitCode -eq 3010) { Write-Warning 'Windows Installer requested a restart.' }
