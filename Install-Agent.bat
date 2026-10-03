@echo off
setlocal EnableExtensions EnableDelayedExpansion
echo ============================================
echo   USB Guardian -- Agent Installation
echo ============================================
echo.

net session >nul 2>&1
if %errorLevel% neq 0 (
    echo ERROR: Please run this script as Administrator.
    pause
    exit /b 1
)

echo [1/7] Checking Windows compatibility and the .NET 8 SDK...
powershell -NoProfile -Command "if ([Environment]::OSVersion.Version.Build -ge 19041) { exit 0 }; exit 1"
if %errorLevel% neq 0 (
    echo ERROR: Device enforcement requires Windows build 19041 or newer.
    pause
    exit /b 1
)
where dotnet >nul 2>&1
if %errorLevel% neq 0 (
    echo ERROR: The dotnet command was not found.
    echo Install the .NET 8 SDK or use the self-contained Endpoint MSI.
    pause
    exit /b 1
)
dotnet --list-sdks 2>nul | findstr /B "8." >nul
if %errorLevel% neq 0 (
    echo ERROR: The .NET 8 SDK is required to publish from source.
    echo Install it or use the self-contained Endpoint MSI.
    pause
    exit /b 1
)
echo        .NET 8 SDK found.

echo.
echo [2/7] Server configuration
set /p SERVER_INPUT="  Server URL or IP/DNS name: "
if "!SERVER_INPUT!"=="" (
    echo ERROR: Server URL cannot be empty.
    pause
    exit /b 1
)
echo(!SERVER_INPUT!| findstr /C:"://" >nul
if %errorLevel% equ 0 (
    set "SERVER_URL=!SERVER_INPUT!"
) else (
    set "SERVER_URL=http://!SERVER_INPUT!:5050"
)
powershell -NoProfile -Command "try { $u=[Uri]$env:SERVER_URL; if ($u.IsAbsoluteUri -and @('http','https') -contains $u.Scheme -and $u.Host -and -not $u.UserInfo -and -not $u.Query -and -not $u.Fragment -and $u.AbsolutePath -eq '/') { exit 0 } } catch {}; exit 1"
if %errorLevel% neq 0 (
    echo ERROR: Enter a valid HTTP or HTTPS server URL.
    pause
    exit /b 1
)
echo        Checking server reachability...
powershell -NoProfile -Command "$url=$env:SERVER_URL.TrimEnd('/') + '/health'; try { $r=Invoke-WebRequest -UseBasicParsing -Uri $url -TimeoutSec 5; if ($r.StatusCode -eq 200) { exit 0 } } catch { Write-Host ('  ' + $_.Exception.Message) }; exit 1"
if %errorLevel% neq 0 (
    echo ERROR: USB Guardian Server is not reachable at !SERVER_URL!.
    echo Confirm the server service is running and TCP port 5050 is allowed.
    pause
    exit /b 1
)
set /p AGENT_TOKEN="  Endpoint token issued by Provision-Endpoint.ps1 for this PC: "
if "!AGENT_TOKEN!"=="" (
    echo ERROR: Agent token cannot be empty.
    pause
    exit /b 1
)

echo [3/7] Stopping existing endpoint processes if present...
net stop "USB Guardian Agent" >nul 2>&1
taskkill /IM USBGuardianNotifier.exe /F >nul 2>&1

echo [4/7] Building and publishing agent...
dotnet publish "%~dp0USBGuardianAgent\USBGuardianAgent.csproj" -c Release -o "%~dp0Dist\Agent"
if %errorLevel% neq 0 (
    echo ERROR: Agent build failed.
    net start "USB Guardian Agent" >nul 2>&1
    pause
    exit /b 1
)

echo [5/7] Building and publishing user notifier...
dotnet publish "%~dp0USBGuardianNotifier\USBGuardianNotifier.csproj" -c Release -o "%~dp0Dist\Notifier"
if %errorLevel% neq 0 (
    echo ERROR: Notifier build failed.
    net start "USB Guardian Agent" >nul 2>&1
    pause
    exit /b 1
)

echo [6/7] Writing agent configuration...
set "CONFIG_DIR=C:\ProgramData\USBGuardian"
set "CONFIG_PATH=%CONFIG_DIR%\agent_config.json"
if not exist "%CONFIG_DIR%" mkdir "%CONFIG_DIR%"
set "MACHINE_ID=%COMPUTERNAME%"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Write-AgentConfig.ps1"
if %errorLevel% neq 0 (
    echo ERROR: Could not write %CONFIG_PATH%.
    net start "USB Guardian Agent" >nul 2>&1
    pause
    exit /b 1
)
powershell -NoProfile -Command "try { $c=Get-Content -Raw $env:CONFIG_PATH | ConvertFrom-Json; if ($c.ServerUrl -eq $env:SERVER_URL -and $c.MachineId -eq $env:MACHINE_ID -and $c.AgentToken -eq $env:AGENT_TOKEN) { exit 0 } } catch {}; exit 1"
if %errorLevel% neq 0 (
    echo ERROR: %CONFIG_PATH% is empty, malformed, or incomplete.
    net start "USB Guardian Agent" >nul 2>&1
    pause
    exit /b 1
)
icacls "%CONFIG_PATH%" /inheritance:r /grant:r "SYSTEM:F" "Administrators:F" >nul
if %errorLevel% neq 0 (
    echo ERROR: Could not protect %CONFIG_PATH%.
    net start "USB Guardian Agent" >nul 2>&1
    pause
    exit /b 1
)

echo [7/7] Registering services and notifier startup...
set "SERVICE_NAME=USB Guardian Agent"
set "EXE_PATH=%~dp0Dist\Agent\USBGuardianAgent.exe"
set "NOTIFIER_PATH=%~dp0Dist\Notifier\USBGuardianNotifier.exe"
sc query "%SERVICE_NAME%" >nul 2>&1
if %errorLevel% neq 0 (
    sc create "%SERVICE_NAME%" binPath= "\"%EXE_PATH%\"" start= auto obj= LocalSystem >nul
) else (
    sc config "%SERVICE_NAME%" binPath= "\"%EXE_PATH%\"" start= auto obj= LocalSystem >nul
)
if %errorLevel% neq 0 (
    echo ERROR: Could not register the agent service.
    net start "USB Guardian Agent" >nul 2>&1
    pause
    exit /b 1
)
sc description "%SERVICE_NAME%" "USB Guardian agent - enforces removable-device policy." >nul
sc failure "%SERVICE_NAME%" reset= 86400 actions= restart/5000/restart/15000/""/0 >nul
if %errorLevel% neq 0 (
    echo ERROR: Could not configure agent service recovery.
    net start "USB Guardian Agent" >nul 2>&1
    pause
    exit /b 1
)
sc failureflag "%SERVICE_NAME%" 1 >nul
if %errorLevel% neq 0 (
    echo ERROR: Could not enable agent recovery for non-zero service exits.
    net start "USB Guardian Agent" >nul 2>&1
    pause
    exit /b 1
)
reg add "HKLM\Software\Microsoft\Windows\CurrentVersion\Run" /v "USBGuardianNotifier" /t REG_SZ /d "\"%NOTIFIER_PATH%\"" /f >nul
if %errorLevel% neq 0 (
    echo ERROR: Could not register the user notifier at logon.
    net start "USB Guardian Agent" >nul 2>&1
    pause
    exit /b 1
)
net start "%SERVICE_NAME%"
if %errorLevel% neq 0 (
    echo ERROR: The agent service did not start. Check Windows Event Viewer.
    pause
    exit /b 1
)

echo.
echo ============================================
echo   Installation Complete
echo ============================================
echo   Agent server: !SERVER_URL!
echo   Machine ID: %COMPUTERNAME%
echo   The notifier starts for each user at next logon.
echo ============================================
pause
