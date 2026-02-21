@echo off
NET SESSION >nul 2>&1
IF %ERRORLEVEL% NEQ 0 (
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)
cd /d "%~dp0"
sc stop "USB Guardian Service"
timeout /t 3 /nobreak
echo Publishing Service...
dotnet publish "USBGuardianService\USBGuardianService.csproj" -c Release -o "Dist\Service"
echo Publishing Control UI...
dotnet publish "USBGuardianControl\USBGuardianControl.csproj" -c Release -o "Dist\Control"
sc start "USB Guardian Service"
echo Build and restart complete.
pause
