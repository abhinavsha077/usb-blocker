@echo off
setlocal EnableExtensions

net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting Administrator privileges to uninstall USB Guardian...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

set "V1_SERVICE=USB Guardian Service"
set "AGENT_SERVICE=USB Guardian Agent"
set "SERVER_SERVICE=USB Guardian Server"
set "HAD_V1=0"
sc query "%V1_SERVICE%" >nul 2>&1
if %errorLevel% equ 0 set "HAD_V1=1"

echo ==============================================================
echo   USB Guardian -- Uninstall And Device Recovery
echo ==============================================================
echo.
echo [1/4] Stopping services before hardware recovery...
net stop "%V1_SERVICE%" >nul 2>&1
net stop "%AGENT_SERVICE%" >nul 2>&1
net stop "%SERVER_SERVICE%" >nul 2>&1
taskkill /IM USBGuardianNotifier.exe /F >nul 2>&1
taskkill /IM USBGuardianControl.exe /F >nul 2>&1

echo [2/4] Restoring devices disabled by USB Guardian...
set "AGENT_EXE=%~dp0Dist\Agent\USBGuardianAgent.exe"
if not exist "%AGENT_EXE%" set "AGENT_EXE=%ProgramFiles%\USB Guardian\Endpoint\USBGuardianAgent.exe"
if exist "%AGENT_EXE%" (
    "%AGENT_EXE%" --restore-managed-devices
    if errorlevel 1 (
        echo WARNING: One or more managed devices could not be restored.
        echo The remaining IDs are retained in:
        echo C:\ProgramData\USBGuardian\agent_managed_state.json
    )
) else (
    echo WARNING: Agent executable not found; managed-device recovery was skipped.
)

if "%HAD_V1%"=="1" (
    echo Restoring USB hubs managed by the legacy v1 service...
    powershell -NoProfile -Command "Get-CimInstance Win32_USBHub | ForEach-Object { $id=$_.DeviceID; if ($id) { & pnputil.exe /enable-device $id | Out-Null } }"
)

echo [3/4] Removing services and notifier startup...
sc delete "%V1_SERVICE%" >nul 2>&1
sc delete "%AGENT_SERVICE%" >nul 2>&1
sc delete "%SERVER_SERVICE%" >nul 2>&1
reg delete "HKLM\Software\Microsoft\Windows\CurrentVersion\Run" /v "USBGuardianNotifier" /f >nul 2>&1
netsh advfirewall firewall delete rule name="USB Guardian Server" >nul 2>&1

echo [4/4] Complete.
echo.
echo ==============================================================
echo USB Guardian services were removed. Policy, inventory, event,
echo and credential files remain under ProgramData for audit.
echo ==============================================================
pause
