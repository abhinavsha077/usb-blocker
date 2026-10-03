@echo off
setlocal EnableExtensions
echo Starting USB Guardian Control Panel...
set "CONTROL_EXE=%~dp0Dist\Control\USBGuardianControl.exe"
if not exist "%CONTROL_EXE%" set "CONTROL_EXE=%ProgramFiles%\USB Guardian\Control\USBGuardianControl.exe"
if not exist "%CONTROL_EXE%" (
    echo ERROR: USB Guardian Control is not published or installed.
    echo Run Update-All.bat or install the Control MSI first.
    pause
    exit /b 1
)
start "" "%CONTROL_EXE%"
if errorlevel 1 (
    echo ERROR: USB Guardian Control could not be started.
    pause
    exit /b 1
)
