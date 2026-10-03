[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Open PowerShell as Administrator and run this script again.'
}

$packages = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '..\Installers') `
    -Filter 'USBGuardian-Control-*-x64.msi' -File)
if ($packages.Count -ne 1) {
    throw "Expected exactly one Control MSI; found $($packages.Count)."
}

$logPath = Join-Path $env:TEMP 'usbguardian-control-install.log'
$arguments = @(
    '/i', "`"$($packages[0].FullName)`"",
    '/qn', '/norestart', '/l*v', "`"$logPath`""
)
$process = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\msiexec.exe') `
    -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
if ($process.ExitCode -notin 0, 3010) {
    throw "Control installation failed with exit code $($process.ExitCode). Log: $logPath"
}

Write-Host 'USB Guardian Control installed. Open it from the Start menu.' -ForegroundColor Green
if ($process.ExitCode -eq 3010) {
    Write-Warning 'Windows Installer requested a restart.'
}
