[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9_-]{16,128}$')]
    [string]$AgentToken = ''
)

$ErrorActionPreference = 'Stop'
$healthUrl = 'http://127.0.0.1:5050/health'
$diagnosticPath = Join-Path $env:TEMP 'usbguardian-server-diagnostics.log'

function Write-ServerDiagnostics {
    param(
        [string]$MsiLogPath,
        [string]$LastHealthError
    )

    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add("USB Guardian Server diagnostics - $(Get-Date -Format o)")
    $lines.Add("Computer: $env:COMPUTERNAME")
    $lines.Add("Health URL: $healthUrl")
    $lines.Add("Last health error: $LastHealthError")
    $lines.Add("MSI log: $MsiLogPath")
    $lines.Add('')
    $lines.Add('=== Service ===')
    $service = Get-CimInstance Win32_Service -ErrorAction SilentlyContinue |
        Where-Object Name -EQ 'USB Guardian Server'
    if ($service) {
        $lines.Add(($service | Select-Object Name, State, StartMode, StartName,
            ExitCode, ProcessId, PathName | Format-List | Out-String))
    }
    else {
        $lines.Add('USB Guardian Server service was not found.')
    }

    $lines.Add('=== Port 5050 ===')
    $listeners = Get-NetTCPConnection -State Listen -LocalPort 5050 `
        -ErrorAction SilentlyContinue
    if ($listeners) {
        $lines.Add(($listeners | Select-Object LocalAddress, LocalPort,
            OwningProcess | Format-Table -AutoSize | Out-String))
    }
    else {
        $lines.Add('No process is listening on TCP 5050.')
    }

    $lines.Add('=== Recent Application events ===')
    $events = Get-WinEvent -FilterHashtable @{
        LogName = 'Application'
        StartTime = (Get-Date).AddMinutes(-15)
    } -ErrorAction SilentlyContinue | Where-Object {
        $_.Message -match 'USBGuardianServer|USB Guardian Server' -or
        $_.ProviderName -eq '.NET Runtime'
    } | Select-Object -First 20 TimeCreated, ProviderName, Id,
        LevelDisplayName, Message
    if ($events) {
        $lines.Add(($events | Format-List | Out-String))
    }
    else {
        $lines.Add('No matching Application events were found.')
    }

    $lines | Set-Content -LiteralPath $diagnosticPath -Encoding utf8
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Open PowerShell as Administrator and run this script again.'
}

$packages = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '..\Installers') `
    -Filter 'USBGuardian-Server-*-x64.msi' -File)
if ($packages.Count -ne 1) {
    throw "Expected exactly one Server MSI; found $($packages.Count)."
}

if ([string]::IsNullOrWhiteSpace($AgentToken)) {
    $AgentToken = [guid]::NewGuid().ToString('N')
}

# A running previous Server version legitimately owns the port during upgrade.
# Any other listener would prevent Kestrel from starting after installation.
$existingService = Get-CimInstance Win32_Service -ErrorAction SilentlyContinue |
    Where-Object Name -EQ 'USB Guardian Server'
$allowedProcessId = if ($existingService) { [uint32]$existingService.ProcessId } else { 0 }
$portConflicts = @(Get-NetTCPConnection -State Listen -LocalPort 5050 `
    -ErrorAction SilentlyContinue | Where-Object {
        $allowedProcessId -eq 0 -or $_.OwningProcess -ne $allowedProcessId
    })
if ($portConflicts.Count -gt 0) {
    $owners = $portConflicts.OwningProcess -join ', '
    throw "TCP port 5050 is already used by process ID(s) $owners. Stop or reconfigure that application before installing USB Guardian Server."
}

$logPath = Join-Path $env:TEMP 'usbguardian-server-install.log'
$arguments = @(
    '/i', "`"$($packages[0].FullName)`"",
    "AGENTTOKEN=$AgentToken",
    '/qn', '/norestart', '/l*v', "`"$logPath`""
)
$process = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\msiexec.exe') `
    -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
if ($process.ExitCode -notin 0, 3010) {
    throw "Server installation failed with exit code $($process.ExitCode). Log: $logPath"
}

$service = Get-Service -Name 'USB Guardian Server' -ErrorAction SilentlyContinue
if (-not $service) {
    Write-ServerDiagnostics -MsiLogPath $logPath `
        -LastHealthError 'The Windows service was not registered.'
    throw "The MSI completed but the USB Guardian Server service was not registered. Diagnostics: $diagnosticPath. MSI log: $logPath"
}

try {
    $service.Refresh()
    if ($service.Status -eq 'Stopped') {
        Start-Service -Name 'USB Guardian Server'
    }
    $service.WaitForStatus(
        'Running',
        [TimeSpan]::FromSeconds(30))
}
catch {
    Write-ServerDiagnostics -MsiLogPath $logPath `
        -LastHealthError $_.Exception.Message
    throw "The USB Guardian Server service did not reach Running state. Diagnostics: $diagnosticPath. MSI log: $logPath"
}

$deadline = (Get-Date).AddSeconds(90)
$healthy = $false
$lastHealthError = 'No health response was received.'
do {
    try {
        # Use IPv4 loopback because Kestrel binds 0.0.0.0 by default. On some
        # Windows systems localhost resolves to IPv6 first or traverses a proxy.
        $response = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 5
        $healthy = $response.status -eq 'healthy'
        if (-not $healthy) {
            $lastHealthError = "Unexpected health status: $($response.status)"
        }
    }
    catch {
        $lastHealthError = $_.Exception.Message
        $service.Refresh()
        if ($service.Status -eq 'Stopped') {
            break
        }
    }
    if (-not $healthy) { Start-Sleep -Milliseconds 500 }
} while (-not $healthy -and (Get-Date) -lt $deadline)

if (-not $healthy) {
    Write-ServerDiagnostics -MsiLogPath $logPath `
        -LastHealthError $lastHealthError
    $service.Refresh()
    throw "The Server installed but $healthUrl did not respond. Service: $($service.Status). Last error: $lastHealthError. Diagnostics: $diagnosticPath. MSI log: $logPath"
}

Write-Host 'USB Guardian Server installed and healthy.' -ForegroundColor Green
Write-Host 'Rotate the default administrator password locally, configure HTTPS, then enroll each endpoint with Provision-Endpoint.ps1.'
Write-Host 'The MSI bootstrap token is for explicit legacy migration only; it is not an endpoint enrollment credential.'
if ($process.ExitCode -eq 3010) {
    Write-Warning 'Windows Installer requested a restart.'
}
