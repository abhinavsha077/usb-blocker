@echo off
setlocal EnableExtensions EnableDelayedExpansion
echo ============================================
echo   USB Guardian -- Server Installation
echo ============================================
echo.

net session >nul 2>&1
if %errorLevel% neq 0 (
    echo ERROR: Please run this script as Administrator.
    pause
    exit /b 1
)

set "SERVICE_NAME=USB Guardian Server"
set "EXE_PATH=%~dp0Dist\Server\USBGuardianServer.exe"
set "TOKEN_FILE=C:\ProgramData\USBGuardian\agent_token.txt"

echo [1/6] Checking for the .NET 8 SDK...
where dotnet >nul 2>&1
if %errorLevel% neq 0 (
    echo ERROR: The dotnet command was not found.
    echo Install the .NET 8 SDK or use the self-contained Server MSI.
    pause
    exit /b 1
)
dotnet --list-sdks 2>nul | findstr /B "8." >nul
if %errorLevel% neq 0 (
    echo ERROR: The .NET 8 SDK is required to publish from source.
    echo Install it or use the self-contained Server MSI.
    pause
    exit /b 1
)

echo [2/6] Stopping existing service if present...
net stop "%SERVICE_NAME%" >nul 2>&1

echo [3/6] Building and publishing server...
dotnet publish "%~dp0USBGuardianServer\USBGuardianServer.csproj" -c Release -o "%~dp0Dist\Server"
if %errorLevel% neq 0 (
    echo ERROR: Server build failed.
    net start "%SERVICE_NAME%" >nul 2>&1
    pause
    exit /b 1
)

echo [4/6] Configuring agent authentication...
if exist "%TOKEN_FILE%" (
    set /p AGENT_TOKEN=<"%TOKEN_FILE%"
)
if "!AGENT_TOKEN!"=="" (
    for /f %%i in ('powershell -NoProfile -Command "[guid]::NewGuid().ToString('N')"') do set "AGENT_TOKEN=%%i"
)
if "!AGENT_TOKEN!"=="" (
    echo ERROR: Could not create an agent token.
    net start "%SERVICE_NAME%" >nul 2>&1
    pause
    exit /b 1
)
if not exist "C:\ProgramData\USBGuardian" (
    mkdir "C:\ProgramData\USBGuardian"
    if !errorLevel! neq 0 (
        echo ERROR: Could not create C:\ProgramData\USBGuardian.
        net start "%SERVICE_NAME%" >nul 2>&1
        pause
        exit /b 1
    )
)
> "%TOKEN_FILE%" echo(!AGENT_TOKEN!
if !errorLevel! neq 0 (
    echo ERROR: Could not write %TOKEN_FILE%.
    net start "%SERVICE_NAME%" >nul 2>&1
    pause
    exit /b 1
)
icacls "%TOKEN_FILE%" /inheritance:r /grant:r "SYSTEM:F" "Administrators:F" >nul
if !errorLevel! neq 0 (
    echo ERROR: Could not protect %TOKEN_FILE%.
    net start "%SERVICE_NAME%" >nul 2>&1
    pause
    exit /b 1
)
set "SERVER_SETTINGS=%~dp0Dist\Server\appsettings.json"
powershell -NoProfile -Command "$p=$env:SERVER_SETTINGS; $j=Get-Content -Raw $p | ConvertFrom-Json; $j | Add-Member -NotePropertyName AgentSecret -NotePropertyValue $env:AGENT_TOKEN -Force; $j | ConvertTo-Json -Depth 10 | Set-Content -Encoding UTF8 $p"
if %errorLevel% neq 0 (
    echo ERROR: Could not write the server agent token.
    net start "%SERVICE_NAME%" >nul 2>&1
    pause
    exit /b 1
)
icacls "%SERVER_SETTINGS%" /inheritance:r /grant:r "SYSTEM:F" "Administrators:F" >nul
if %errorLevel% neq 0 (
    echo ERROR: Could not protect the server settings file.
    net start "%SERVICE_NAME%" >nul 2>&1
    pause
    exit /b 1
)

echo [5/6] Opening Windows Firewall for TCP port 5050...
netsh advfirewall firewall delete rule name="USB Guardian Server" >nul 2>&1
netsh advfirewall firewall add rule name="USB Guardian Server" dir=in action=allow protocol=TCP localport=5050 >nul
if %errorLevel% neq 0 (
    echo ERROR: Could not create the Windows Firewall rule.
    net start "%SERVICE_NAME%" >nul 2>&1
    pause
    exit /b 1
)

echo [6/6] Registering and starting Windows Service...
sc query "%SERVICE_NAME%" >nul 2>&1
if %errorLevel% neq 0 (
    sc create "%SERVICE_NAME%" binPath= "\"%EXE_PATH%\"" start= auto obj= LocalSystem >nul
) else (
    sc config "%SERVICE_NAME%" binPath= "\"%EXE_PATH%\"" start= auto obj= LocalSystem >nul
)
if %errorLevel% neq 0 (
    echo ERROR: Could not register the server service.
    net start "%SERVICE_NAME%" >nul 2>&1
    pause
    exit /b 1
)
sc description "%SERVICE_NAME%" "USB Guardian central server - manages endpoint USB policies." >nul
sc failure "%SERVICE_NAME%" reset= 86400 actions= restart/5000/restart/15000/""/0 >nul
if %errorLevel% neq 0 (
    echo ERROR: Could not configure server service recovery.
    net start "%SERVICE_NAME%" >nul 2>&1
    pause
    exit /b 1
)
sc failureflag "%SERVICE_NAME%" 1 >nul
if %errorLevel% neq 0 (
    echo ERROR: Could not enable server recovery for non-zero service exits.
    net start "%SERVICE_NAME%" >nul 2>&1
    pause
    exit /b 1
)
net start "%SERVICE_NAME%"
if %errorLevel% neq 0 (
    echo ERROR: The server service did not start. Check Windows Event Viewer.
    pause
    exit /b 1
)
powershell -NoProfile -Command "$deadline=(Get-Date).AddSeconds(15); do { try { $r=Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:5050/health' -TimeoutSec 2; if ($r.StatusCode -eq 200) { exit 0 } } catch {}; Start-Sleep -Milliseconds 500 } while ((Get-Date) -lt $deadline); exit 1"
if %errorLevel% neq 0 (
    echo ERROR: The service started but did not open http://127.0.0.1:5050.
    echo Check Windows Event Viewer for a USB Guardian Server startup error.
    pause
    exit /b 1
)

echo.
echo ============================================
echo   Installation Complete
echo ============================================
echo   Server URL: http://%COMPUTERNAME%:5050
echo   Default admin password: admin
echo.
echo   The legacy bootstrap token is retained in a protected file:
echo   %TOKEN_FILE%
echo.
echo   Change the admin password from the dashboard
echo   before managing endpoints remotely.
echo   Issue a separate token for each PC with Provision-Endpoint.ps1.
echo   See docs\EndpointEnrollment.md for provisioning and HTTPS.
echo ============================================
pause
