[CmdletBinding()]
param(
    [string]$ServerUrl = 'http://localhost:5050'
)

$ErrorActionPreference = 'Stop'
$ServerUrl = $ServerUrl.Trim().TrimEnd('/')

Write-Host "Checking $ServerUrl/health ..."
$health = Invoke-RestMethod -Uri ($ServerUrl + '/health') -TimeoutSec 8
if ($health.status -ne 'healthy') {
    throw 'Server health check did not return healthy.'
}
Write-Host 'Server health: PASS' -ForegroundColor Green

foreach ($serviceName in 'USB Guardian Server', 'USB Guardian Agent') {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($service) {
        $color = if ($service.Status -eq 'Running') { 'Green' } else { 'Red' }
        Write-Host "$serviceName service: $($service.Status)" -ForegroundColor $color
    }
}

$agentKey = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\USBGuardian\Agent' `
    -ErrorAction SilentlyContinue
if ($agentKey) {
    Write-Host "Configured Agent Server URL: $($agentKey.ServerUrl)"
    Write-Host "Agent token configured: $(-not [string]::IsNullOrWhiteSpace($agentKey.AgentToken))"
}

Write-Host 'Open the dashboard to confirm Agent Online state and policy reporting.'
