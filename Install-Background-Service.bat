@echo off

:: Check for privileges
NET SESSION >nul 2>&1
IF %ERRORLEVEL% NEQ 0 (
    echo Requesting Administrator privileges to permanently install the USB Service...
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

set SERVICE_NAME=USB Guardian Service
set BIN_PATH="%~dp0Dist\Service\USBGuardianService.exe"

echo Stopping service if already installed...
net stop "%SERVICE_NAME%" 2>nul
sc delete "%SERVICE_NAME%" 2>nul

echo Installing %SERVICE_NAME% to run in the background on startup...
sc create "%SERVICE_NAME%" binPath= %BIN_PATH% start= auto obj= LocalSystem

echo Starting the service in the background...
sc start "%SERVICE_NAME%"

echo.
echo ==============================================================
echo SUCCESS: The USB Guardian Service is now installed and running.
echo It will now run invisibly in the background forever (even
echo after you reboot your PC). You will no longer see the black
echo console window.
echo ==============================================================
pause
