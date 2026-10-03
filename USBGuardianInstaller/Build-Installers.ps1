[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.1.0',

    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [ValidateSet('win-x64')]
    [string]$RuntimeIdentifier = 'win-x64',

    [string]$CertificateThumbprint = '',

    [string]$TimestampUrl = 'http://timestamp.digicert.com',

    [switch]$Production
)

$ErrorActionPreference = 'Stop'
if ($Production -and [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    throw 'Production builds require -CertificateThumbprint for trusted Authenticode signing.'
}
if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) {
    throw 'WiX MSI linking is supported only on Windows. Run this script on Windows 10/11 or Windows Server.'
}

$installerRoot = $PSScriptRoot
$repositoryRoot = Split-Path $installerRoot -Parent
$artifactRoot = Join-Path $installerRoot 'artifacts'
$outputRoot = Join-Path $installerRoot 'output'

function Invoke-DotNet {
    param([string[]]$DotNetArguments)
    & dotnet @DotNetArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed with exit code $LASTEXITCODE."
    }
}

function Publish-SingleFile {
    param(
        [string]$Project,
        [string]$Output
    )

    Invoke-DotNet -DotNetArguments @(
        'publish', $Project,
        '--configuration', $Configuration,
        '--runtime', $RuntimeIdentifier,
        '--self-contained', 'true',
        '--output', $Output,
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugSymbols=false',
        '-p:DebugType=None',
        '-p:EnableWindowsTargeting=true'
    )
}

function Invoke-SignTool {
    param([string[]]$Paths)

    if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
        Write-Warning 'Artifacts are unsigned. Supply -CertificateThumbprint for production.'
        return
    }
    $signTool = Get-Command signtool.exe -ErrorAction Stop
    foreach ($path in $Paths) {
        & $signTool.Source sign /sha1 $CertificateThumbprint /fd SHA256 `
            /tr $TimestampUrl /td SHA256 $path
        if ($LASTEXITCODE -ne 0) {
            throw "signtool failed for $path."
        }
        if ($Production) {
            & $signTool.Source verify /pa /all $path
            if ($LASTEXITCODE -ne 0) { throw "Production signature verification failed for $path." }
        }
    }
}

foreach ($generatedDirectory in @($artifactRoot, $outputRoot)) {
    $resolvedGeneratedDirectory = [IO.Path]::GetFullPath($generatedDirectory)
    $resolvedInstallerRoot = [IO.Path]::GetFullPath($installerRoot).TrimEnd('\') + '\'
    if (-not $resolvedGeneratedDirectory.StartsWith($resolvedInstallerRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Generated directories must remain within the installer workspace.'
    }
    if (Test-Path $generatedDirectory) {
        Remove-Item -LiteralPath $generatedDirectory -Recurse -Force
    }
}
New-Item -ItemType Directory -Force -Path $artifactRoot, $outputRoot | Out-Null

$serverOutput = Join-Path $artifactRoot 'Server'
$endpointOutput = Join-Path $artifactRoot 'Endpoint'
$controlOutput = Join-Path $artifactRoot 'Control'
New-Item -ItemType Directory -Force `
    -Path $serverOutput, $endpointOutput, $controlOutput | Out-Null

Publish-SingleFile `
    (Join-Path $repositoryRoot 'USBGuardianServer/USBGuardianServer.csproj') `
    $serverOutput
Publish-SingleFile `
    (Join-Path $repositoryRoot 'USBGuardianAgent/USBGuardianAgent.csproj') `
    $endpointOutput
Publish-SingleFile `
    (Join-Path $repositoryRoot 'USBGuardianNotifier/USBGuardianNotifier.csproj') `
    $endpointOutput
Publish-SingleFile `
    (Join-Path $repositoryRoot 'USBGuardianControl/USBGuardianControl.csproj') `
    $controlOutput

$payloadExecutables = @(
    (Join-Path $serverOutput 'USBGuardianServer.exe'),
    (Join-Path $endpointOutput 'USBGuardianAgent.exe'),
    (Join-Path $endpointOutput 'USBGuardianNotifier.exe'),
    (Join-Path $controlOutput 'USBGuardianControl.exe')
)
Invoke-SignTool $payloadExecutables

$projects = @(
    (Join-Path $installerRoot 'Server/USBGuardianServerInstaller.wixproj'),
    (Join-Path $installerRoot 'Endpoint/USBGuardianEndpointInstaller.wixproj'),
    (Join-Path $installerRoot 'Control/USBGuardianControlInstaller.wixproj')
)
foreach ($project in $projects) {
    # WiX's retained project intermediates can consider linking up to date after
    # a version/output change, then copy an MSI that was never produced. Keep
    # linking intermediates inside the freshly cleared build artifact directory.
    $role = Split-Path (Split-Path $project -Parent) -Leaf
    $wixIntermediate = Join-Path (Join-Path $artifactRoot 'wix') $role
    Invoke-DotNet -DotNetArguments @(
        'build', $project,
        '--configuration', $Configuration,
        "-p:ProductVersion=$Version",
        "-p:PayloadRoot=$artifactRoot",
        "-p:IntermediateOutputPath=$wixIntermediate/",
        "-p:OutputPath=$outputRoot"
    )
}

$packages = @(
    (Join-Path $outputRoot "USBGuardian-Server-$Version-x64.msi"),
    (Join-Path $outputRoot "USBGuardian-Endpoint-$Version-x64.msi"),
    (Join-Path $outputRoot "USBGuardian-Control-$Version-x64.msi")
)
foreach ($package in $packages) {
    if (-not (Test-Path $package -PathType Leaf)) {
        throw "Expected package was not created: $package"
    }
}
Invoke-SignTool $packages

$hashes = $packages |
    Sort-Object |
    ForEach-Object {
        $hash = Get-FileHash $_ -Algorithm SHA256
        "$($hash.Hash)  $(Split-Path $_ -Leaf)"
    }
Set-Content `
    -Path (Join-Path $outputRoot 'SHA256SUMS.txt') `
    -Value $hashes `
    -Encoding ascii

# Stage instructions with the same packages, so a fresh bundle cannot silently
# reuse the old shared-token deployment instructions.
$bundleRoot = Join-Path $outputRoot 'Bundle'
$bundleInstallers = Join-Path $bundleRoot 'Installers'
$bundleTools = Join-Path $bundleRoot 'Tools'
$bundleDocumentation = Join-Path $bundleRoot 'Documentation'
New-Item -ItemType Directory -Force -Path $bundleRoot, $bundleInstallers, $bundleTools, $bundleDocumentation | Out-Null
Get-ChildItem -LiteralPath (Join-Path $installerRoot 'ReleaseBundle') | Copy-Item -Destination $bundleRoot -Recurse -Force
Copy-Item -LiteralPath $packages -Destination $bundleInstallers
Copy-Item -LiteralPath (Join-Path $outputRoot 'SHA256SUMS.txt') -Destination $bundleInstallers
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'Provision-Endpoint.ps1') -Destination $bundleTools
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'Write-AgentConfig.ps1') -Destination $bundleTools
foreach ($guide in @('EndpointEnrollment.md', 'DeploymentGuide.md', 'MSI_GPO_DEPLOYMENT.md', 'Troubleshooting.md')) {
    Copy-Item -LiteralPath (Join-Path (Join-Path $repositoryRoot 'docs') $guide) -Destination $bundleDocumentation
}
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'USBGuardianServer/appsettings.Secure.example.json') -Destination $bundleDocumentation
foreach ($document in Get-ChildItem -LiteralPath $bundleRoot -Filter '*.md') {
    (Get-Content -LiteralPath $document.FullName -Raw).Replace('{{VERSION}}', $Version) |
        Set-Content -LiteralPath $document.FullName -Encoding UTF8
}

Write-Host "Installers created in $outputRoot"
