@echo off

:: Check for privileges
NET SESSION >nul 2>&1
IF %ERRORLEVEL% NEQ 0 (
    echo Requesting Administrator privileges to disable/remove the USB Service...
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

set SERVICE_NAME=USB Guardian Service

echo Stopping service...
net stop "%SERVICE_NAME%" 2>nul

echo Removing %SERVICE_NAME%...
sc delete "%SERVICE_NAME%" 2>nul

echo.
echo ==============================================================
echo SUCCESS: The USB Guardian Service has been stopped and removed.
echo It will no longer start automatically when your PC restarts.
echo ==============================================================
pause
