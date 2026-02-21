@echo off
echo ============================================
echo   USB Guardian — Server Installation
echo ============================================
echo.

:: Require admin
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo ERROR: Please run this script as Administrator.
    pause & exit /b 1
)

set SERVICE_NAME=USB Guardian Server
set EXE_PATH=%~dp0Dist\Server\USBGuardianServer.exe

echo [1/4] Stopping existing service (if any)...
net stop "%SERVICE_NAME%" 2>nul

echo [2/4] Building and publishing server...
dotnet publish "%~dp0USBGuardianServer\USBGuardianServer.csproj" -c Release -o "%~dp0Dist\Server"
if %errorLevel% neq 0 ( echo BUILD FAILED. & pause & exit /b 1 )

echo [3/5] Opening Windows Firewall for port 5050...
netsh advfirewall firewall delete rule name="USB Guardian Server" >nul 2>&1
netsh advfirewall firewall add rule name="USB Guardian Server" dir=in action=allow protocol=TCP localport=5050
echo.
echo [4/4] Registering Windows Service...
sc delete "%SERVICE_NAME%" 2>nul
sc create "%SERVICE_NAME%" binPath="%EXE_PATH%" start=auto obj=LocalSystem
sc description "%SERVICE_NAME%" "USB Guardian central server - manages all USB agents on the LAN."

echo [4/4] Starting service...
net start "%SERVICE_NAME%"

echo.
echo Server is running on port 5050.
echo Admin UI should connect to: http://%COMPUTERNAME%:5050
echo Default password: admin
echo.
pause
