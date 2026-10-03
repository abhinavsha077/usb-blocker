[CmdletBinding()]
param(
    [Parameter(Mandatory)][uri]$ServerUrl,
    [Parameter(Mandatory)][ValidateLength(1,128)][string]$MachineId,
    [Parameter(Mandatory)][string]$OutputPath,
    [Security.SecureString]$AdminPassword,
    [switch]$AllowHttp
)
$ErrorActionPreference = 'Stop'
if (-not $ServerUrl.IsAbsoluteUri -or $ServerUrl.Scheme -notin @('http','https') -or
    $ServerUrl.UserInfo -or $ServerUrl.Query -or $ServerUrl.Fragment -or $ServerUrl.AbsolutePath -ne '/') {
    throw 'ServerUrl must be the HTTP(S) origin, with no credentials, path, query, or fragment.'
}
if ($ServerUrl.Scheme -ne 'https' -and -not $AllowHttp) {
    throw 'Use HTTPS, or explicitly select -AllowHttp for the LAN compatibility mode.'
}
if ($MachineId -ne $MachineId.Trim() -or $MachineId -match '[\x00-\x1f\x7f/\\]') {
    throw 'MachineId must exactly match the endpoint identity, without surrounding whitespace or separators.'
}
$destination = [IO.Path]::GetFullPath($OutputPath)
$parent = [IO.Path]::GetDirectoryName($destination)
[IO.Directory]::CreateDirectory($parent) | Out-Null
if (-not $AdminPassword) { $AdminPassword = Read-Host 'Server administrator password' -AsSecureString }
$tempPath = $destination + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
$security = New-Object Security.AccessControl.FileSecurity
$security.SetSecurityDescriptorSddlForm('D:P(A;;FA;;;SY)(A;;FA;;;BA)')
$currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
$security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($currentSid, 'FullControl', 'Allow')))
$provisionIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
if ((New-Object Security.Principal.WindowsPrincipal($provisionIdentity)).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $security.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
}
# Establish a protected destination before issuing (or rotating) a credential.
if ($PSVersionTable.PSVersion.Major -ge 7) {
    $stream = [IO.FileSystemAclExtensions]::Create((New-Object IO.FileInfo($tempPath)),
        [IO.FileMode]::CreateNew, [Security.AccessControl.FileSystemRights]::FullControl,
        [IO.FileShare]::None, 4096, [IO.FileOptions]::None, $security)
} else {
    $stream = New-Object IO.FileStream($tempPath, [IO.FileMode]::CreateNew,
        [Security.AccessControl.FileSystemRights]::FullControl, [IO.FileShare]::None,
        4096, [IO.FileOptions]::None, $security)
}
$credentialIssued = $false
$credentialWritten = $false
$outputCommitted = $false
try {
    $plainPassword = (New-Object Net.NetworkCredential('', $AdminPassword)).Password
    $endpoint = $ServerUrl.AbsoluteUri.TrimEnd('/') + '/api/agents/' + [Uri]::EscapeDataString($MachineId) + '/credential'
    $issued = Invoke-RestMethod -Uri $endpoint -Method Post -TimeoutSec 15 -Headers @{ 'X-Admin-Password' = $plainPassword }
    if ($issued.machineId -ne $MachineId -or $issued.agentToken -notmatch '^[A-Fa-f0-9]{64}$') {
        throw 'Server returned an invalid endpoint credential.'
    }
    $credentialIssued = $true
    $json = [ordered]@{ ServerUrl=$ServerUrl.AbsoluteUri.TrimEnd('/'); MachineId=$MachineId;
        AgentToken=$issued.agentToken; RequireHttps=($ServerUrl.Scheme -eq 'https') } | ConvertTo-Json
    $writer = New-Object IO.StreamWriter($stream)
    $writer.Write($json)
    $writer.Flush()
    $stream.Flush($true)
    $credentialWritten = $true
    $writer.Dispose()
    $stream = $null
    Move-Item -LiteralPath $tempPath -Destination $destination -Force
    $outputCommitted = $true
    Write-Host "Endpoint credential saved to $destination. Reissuing revokes the old credential."
}
finally {
    $plainPassword = $null
    $issued = $null
    if ($stream) { $stream.Dispose() }
    if ($credentialIssued -and -not $outputCommitted) {
        if ($credentialWritten) {
            Write-Warning "Credential rotated, but destination replacement failed. Recover the protected configuration from $tempPath."
        } else {
            Write-Warning "Credential rotated, but its configuration could not be fully saved. Repair storage and reissue for $MachineId. Partial protected file retained at $tempPath."
        }
    } elseif (Test-Path -LiteralPath $tempPath) {
        Remove-Item -LiteralPath $tempPath -Force
    }
}
