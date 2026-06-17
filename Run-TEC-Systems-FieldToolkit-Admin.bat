@echo off
setlocal

REM Launches TEC Systems Field Toolkit elevated for IP Shifter actions.
set "SCRIPT_DIR=%~dp0"
set "SCRIPT_PATH=%SCRIPT_DIR%TEC-Systems-FieldToolkit.ps1"

if not exist "%SCRIPT_PATH%" (
    echo Could not find "%SCRIPT_PATH%"
    pause
    exit /b 1
)

cd /d "%SCRIPT_DIR%"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList '-NoLogo -NoProfile -ExecutionPolicy Bypass -STA -File ""%SCRIPT_PATH%""'"
exit /b %ERRORLEVEL%
