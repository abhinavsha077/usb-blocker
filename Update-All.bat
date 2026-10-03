@echo off
setlocal EnableExtensions
echo ============================================
echo   USB Guardian -- Update All Components
echo ============================================
echo.

net session >nul 2>&1
if %errorLevel% neq 0 (
    echo ERROR: Please run this script as Administrator.
    pause
    exit /b 1
)

cd /d "%~dp0"

echo [1/6] Checking for the .NET 8 SDK...
where dotnet >nul 2>&1
if %errorLevel% neq 0 (
    echo ERROR: The dotnet command was not found.
    pause
    exit /b 1
)
dotnet --list-sdks 2>nul | findstr /B "8." >nul
if %errorLevel% neq 0 (
    echo ERROR: The .NET 8 SDK is required to publish from source.
    pause
    exit /b 1
)

echo [2/6] Stopping services...
net stop "USB Guardian Server" >nul 2>&1
net stop "USB Guardian Agent" >nul 2>&1
taskkill /IM USBGuardianNotifier.exe /F >nul 2>&1
taskkill /IM USBGuardianControl.exe /F >nul 2>&1

echo [3/6] Publishing Server...
dotnet publish "USBGuardianServer\USBGuardianServer.csproj" -c Release -o "Dist\Server"
if %errorLevel% neq 0 (
    echo ERROR: Server build failed.
    net start "USB Guardian Server" >nul 2>&1
    net start "USB Guardian Agent" >nul 2>&1
    pause
    exit /b 1
)

echo [4/6] Publishing Agent...
dotnet publish "USBGuardianAgent\USBGuardianAgent.csproj" -c Release -o "Dist\Agent"
if %errorLevel% neq 0 (
    echo ERROR: Agent build failed.
    net start "USB Guardian Server" >nul 2>&1
    net start "USB Guardian Agent" >nul 2>&1
    pause
    exit /b 1
)

echo [5/6] Publishing Control UI...
dotnet publish "USBGuardianControl\USBGuardianControl.csproj" -c Release -o "Dist\Control"
if %errorLevel% neq 0 (
    echo ERROR: Control UI build failed.
    net start "USB Guardian Server" >nul 2>&1
    net start "USB Guardian Agent" >nul 2>&1
    pause
    exit /b 1
)

echo [6/6] Publishing Notifier...
dotnet publish "USBGuardianNotifier\USBGuardianNotifier.csproj" -c Release -o "Dist\Notifier"
if %errorLevel% neq 0 (
    echo ERROR: Notifier build failed.
    net start "USB Guardian Server" >nul 2>&1
    net start "USB Guardian Agent" >nul 2>&1
    pause
    exit /b 1
)
reg add "HKLM\Software\Microsoft\Windows\CurrentVersion\Run" /v "USBGuardianNotifier" /t REG_SZ /d "\"%~dp0Dist\Notifier\USBGuardianNotifier.exe\"" /f >nul
if errorlevel 1 (
    echo ERROR: Could not refresh the user notifier startup entry.
    net start "USB Guardian Server" >nul 2>&1
    net start "USB Guardian Agent" >nul 2>&1
    pause
    exit /b 1
)

echo.
echo Starting installed services...
sc query "USB Guardian Server" >nul 2>&1
if not errorlevel 1 (
    sc failure "USB Guardian Server" reset= 86400 actions= restart/5000/restart/15000/""/0 >nul
    if errorlevel 1 (
        echo ERROR: Could not configure server service recovery.
        net start "USB Guardian Server" >nul 2>&1
        net start "USB Guardian Agent" >nul 2>&1
        exit /b 1
    )
    sc failureflag "USB Guardian Server" 1 >nul
    if errorlevel 1 (
        echo ERROR: Could not enable server recovery for non-zero exits.
        net start "USB Guardian Server" >nul 2>&1
        net start "USB Guardian Agent" >nul 2>&1
        exit /b 1
    )
    net start "USB Guardian Server"
    if errorlevel 1 (
        echo ERROR: The server service did not restart.
        net start "USB Guardian Agent" >nul 2>&1
        exit /b 1
    )
)
sc query "USB Guardian Agent" >nul 2>&1
if not errorlevel 1 (
    sc failure "USB Guardian Agent" reset= 86400 actions= restart/5000/restart/15000/""/0 >nul
    if errorlevel 1 (
        echo ERROR: Could not configure agent service recovery.
        net start "USB Guardian Agent" >nul 2>&1
        exit /b 1
    )
    sc failureflag "USB Guardian Agent" 1 >nul
    if errorlevel 1 (
        echo ERROR: Could not enable agent recovery for non-zero exits.
        net start "USB Guardian Agent" >nul 2>&1
        exit /b 1
    )
    net start "USB Guardian Agent"
    if errorlevel 1 (
        echo ERROR: The agent service did not restart.
        exit /b 1
    )
)

echo.
echo ============================================
echo   Update Complete
echo ============================================
echo   Server, Agent, Control, and Notifier were
echo   published. Installed services were restarted.
echo ============================================
pause
