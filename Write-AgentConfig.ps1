# Called by Install-Agent.bat. Secrets arrive through the process environment,
# never command-line arguments, and the file has its final ACL before any write.
$ErrorActionPreference = 'Stop'
$server = [uri]$env:SERVER_URL
if (-not $server.IsAbsoluteUri -or $server.Scheme -notin @('http', 'https') -or
    $server.UserInfo -or $server.Query -or $server.Fragment -or $server.AbsolutePath -ne '/') {
    throw 'SERVER_URL must be an HTTP(S) origin without credentials, path, query or fragment.'
}
if ([string]::IsNullOrWhiteSpace($env:MACHINE_ID) -or
    [string]::IsNullOrWhiteSpace($env:AGENT_TOKEN)) {
    throw 'Machine identity and enrolled token are required.'
}
$destination = [IO.Path]::GetFullPath($env:CONFIG_PATH)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
# File ACLs cannot prevent replacement through a writable parent directory.
if ([IO.Path]::GetDirectoryName($destination) -eq 'C:\ProgramData\USBGuardian') {
    $directorySecurity = New-Object Security.AccessControl.DirectorySecurity
    $directorySecurity.SetSecurityDescriptorSddlForm('D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;GRGX;;;BU)')
    $directorySecurity.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
    $info = New-Object IO.DirectoryInfo([IO.Path]::GetDirectoryName($destination))
    if (($info.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Configuration directory must not be a link.' }
    if ($PSVersionTable.PSVersion.Major -ge 7) {
        [IO.FileSystemAclExtensions]::SetAccessControl($info, $directorySecurity)
    } else { $info.SetAccessControl($directorySecurity) }
}
$temporary = $destination + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
$security = New-Object Security.AccessControl.FileSecurity
$security.SetSecurityDescriptorSddlForm('D:P(A;;FA;;;SY)(A;;FA;;;BA)')
$security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
    [Security.Principal.WindowsIdentity]::GetCurrent().User, 'FullControl', 'Allow')))
$writerIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
if ((New-Object Security.Principal.WindowsPrincipal($writerIdentity)).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $security.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
}
$stream = $null
$writer = $null
try {
    if ($PSVersionTable.PSVersion.Major -ge 7) {
        $stream = [IO.FileSystemAclExtensions]::Create((New-Object IO.FileInfo($temporary)),
            [IO.FileMode]::CreateNew, [Security.AccessControl.FileSystemRights]::FullControl,
            [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough, $security)
    } else {
        $stream = New-Object IO.FileStream($temporary, [IO.FileMode]::CreateNew,
            [Security.AccessControl.FileSystemRights]::FullControl, [IO.FileShare]::None,
            4096, [IO.FileOptions]::WriteThrough, $security)
    }
    $json = [ordered]@{
        ServerUrl = $server.AbsoluteUri.TrimEnd('/')
        MachineId = $env:MACHINE_ID
        AgentToken = $env:AGENT_TOKEN
        RequireHttps = ($server.Scheme -eq 'https')
    } | ConvertTo-Json
    $writer = New-Object IO.StreamWriter($stream)
    $writer.Write($json)
    $writer.Flush()
    $stream.Flush($true)
    $writer.Dispose()
    $writer = $null
    $stream = $null
    Move-Item -LiteralPath $temporary -Destination $destination -Force
} finally {
    if ($writer) { $writer.Dispose() }
    if ($stream) { $stream.Dispose() }
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
}
