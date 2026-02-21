@echo off
setlocal EnableDelayedExpansion
echo ============================================
echo   USB Guardian -- Agent Installation
echo ============================================
echo.

:: Require admin
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo ERROR: Please run this script as Administrator.
    pause & exit /b 1
)

:: ─── STEP 1: Check / Install .NET 8 Runtime ─────────────────────────────────
echo [1/6] Checking for .NET 8 Runtime...
dotnet --list-runtimes 2>nul | findstr /C:"Microsoft.NETCore.App 8." >nul 2>&1
if %errorLevel% equ 0 (
    echo        .NET 8 Runtime found. Skipping install.
) else (
    echo        .NET 8 Runtime NOT found. Looking for installer...
    set "SDK_INSTALLER=%~dp0dotnet-sdk-8.0.418-win-x64.exe"
    if exist "!SDK_INSTALLER!" (
        echo        Found dotnet-sdk-8.0.418-win-x64.exe. Installing silently...
        "!SDK_INSTALLER!" /install /quiet /norestart
        if !errorLevel! neq 0 (
            echo ERROR: .NET SDK installation failed. Try installing manually.
            pause & exit /b 1
        )
        echo        .NET 8 installed successfully.
    ) else (
        echo ERROR: dotnet-sdk-8.0.418-win-x64.exe not found in the same folder.
        echo        Please place the installer next to this script and try again.
        echo        Download from: https://dotnet.microsoft.com/en-us/download/dotnet/8.0
        pause & exit /b 1
    )
)

:: ─── STEP 2: Ask for Server IP ───────────────────────────────────────────────
echo.
echo [2/6] Server Configuration
echo ─────────────────────────────────────────────
echo  Enter the IP address of the machine running
echo  the USB Guardian SERVER (e.g. 192.168.1.101)
echo.
set /p SERVER_IP="  Server IP: "

if "!SERVER_IP!"=="" (
    echo ERROR: Server IP cannot be empty.
    pause & exit /b 1
)

echo        Server URL will be: http://!SERVER_IP!:5050
echo.

:: ─── STEP 3: Stop old agent ──────────────────────────────────────────────────
echo [3/6] Stopping existing agent service (if any)...
net stop "USB Guardian Agent" 2>nul

:: ─── STEP 4: Build and publish ──────────────────────────────────────────────
echo [4/6] Building and publishing agent...
dotnet publish "%~dp0USBGuardianAgent\USBGuardianAgent.csproj" -c Release -o "%~dp0Dist\Agent"
if %errorLevel% neq 0 ( echo BUILD FAILED. & pause & exit /b 1 )

:: ─── STEP 5: Write agent_config.json with entered IP ────────────────────────
echo [5/6] Writing agent configuration...
set "CONFIG_DIR=C:\ProgramData\USBGuardian"
set "CONFIG_PATH=%CONFIG_DIR%\agent_config.json"
mkdir "%CONFIG_DIR%" 2>nul

(
echo {
echo   "ServerUrl": "http://!SERVER_IP!:5050",
echo   "MachineId": "%COMPUTERNAME%",
echo   "AgentToken": "default-token"
echo }
) > "%CONFIG_PATH%"

echo        Config written to %CONFIG_PATH%
echo        MachineId: %COMPUTERNAME%
echo        ServerUrl: http://!SERVER_IP!:5050

:: ─── STEP 6: Register and start Windows Service ─────────────────────────────
echo [6/6] Registering and starting Windows Service...
set "SERVICE_NAME=USB Guardian Agent"
set "EXE_PATH=%~dp0Dist\Agent\USBGuardianAgent.exe"

sc delete "%SERVICE_NAME%" 2>nul
sc create "%SERVICE_NAME%" binPath="%EXE_PATH%" start=auto obj=LocalSystem
sc description "%SERVICE_NAME%" "USB Guardian agent - enforces USB port policies on this machine."
net start "%SERVICE_NAME%"

echo.
echo ============================================
echo   Installation Complete!
echo ============================================
echo   Agent is running and connecting to:
echo   http://!SERVER_IP!:5050
echo.
echo   This machine (%COMPUTERNAME%) will appear
echo   in the USB Guardian Admin Control Panel
echo   within 10 seconds.
echo ============================================
echo.
pause
