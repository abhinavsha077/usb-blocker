$files = Get-ChildItem -Path "t:\ArcX\USB Blocker" -Recurse -File | Where-Object { $_.FullName -notmatch "\\(bin|obj|Dist|USBGuardianInstaller)\\.*" -and $_.FullName -notmatch "\.git" }
foreach ($f in $files) {
    $lines = 0
    if ($f.Extension -match "\.(cs|xaml|bat|md|sln|json|csproj)$") {
        $lines = (Get-Content $f.FullName | Measure-Object -Line).Lines
    }
    $relPath = $f.FullName.Replace("t:\ArcX\USB Blocker\", "")
    Write-Output "$relPath|Size: $($f.Length) bytes|Lines: $lines"
}
