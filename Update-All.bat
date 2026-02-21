@echo off
setlocal EnableDelayedExpansion
echo ============================================
echo   USB Guardian -- Update All Components
echo ============================================
echo.

:: Require admin
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo ERROR: Please run this script as Administrator.
    pause & exit /b 1
)

cd /d "%~dp0"

echo [1/4] Stopping services...
net stop "USB Guardian Server" 2>nul
net stop "USB Guardian Agent" 2>nul
timeout /t 2 /nobreak >nul

echo [2/4] Building Server...
dotnet publish "USBGuardianServer\USBGuardianServer.csproj" -c Release -o "Dist\Server"
if %errorLevel% neq 0 ( echo ERROR: Server build failed. & pause & exit /b 1 )

echo [3/4] Building Agent...
dotnet publish "USBGuardianAgent\USBGuardianAgent.csproj" -c Release -o "Dist\Agent"
if %errorLevel% neq 0 ( echo ERROR: Agent build failed. & pause & exit /b 1 )

echo [4/4] Building Control UI...
dotnet publish "USBGuardianControl\USBGuardianControl.csproj" -c Release -o "Dist\Control"
if %errorLevel% neq 0 ( echo ERROR: Control UI build failed. & pause & exit /b 1 )

echo.
echo Starting services...
net start "USB Guardian Server"
net start "USB Guardian Agent"

echo.
echo ============================================
echo   UPDATE COMPLETE!
echo ============================================
echo   Server, Agent, and Control UI have been
echo   compiled and restarted.
echo ============================================
echo.
pause
