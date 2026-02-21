@echo off
NET SESSION >nul 2>&1
IF %ERRORLEVEL% NEQ 0 (
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)
cd /d "%~dp0"
sc stop "USB Guardian Service"
timeout /t 3 /nobreak
dotnet publish "USBGuardianService\USBGuardianService.csproj" -c Release -o "Dist\Service"
sc start "USB Guardian Service"
echo Build and restart complete.
pause
