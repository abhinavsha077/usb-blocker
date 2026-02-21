@echo off
NET SESSION >nul 2>&1
IF %ERRORLEVEL% NEQ 0 (
    echo Requesting Administrator privileges to run the USB Service...
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)
echo Starting USB Guardian Service...
cd /d "%~dp0Dist\Service"
USBGuardianService.exe
pause
