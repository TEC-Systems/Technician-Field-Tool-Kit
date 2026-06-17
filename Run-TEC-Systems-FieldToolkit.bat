@echo off
setlocal

REM Launches TEC Systems Field Toolkit from the same folder as this batch file.
set "SCRIPT_DIR=%~dp0"
set "SCRIPT_PATH=%SCRIPT_DIR%TEC-Systems-FieldToolkit.ps1"

if not exist "%SCRIPT_PATH%" (
    echo Could not find "%SCRIPT_PATH%"
    pause
    exit /b 1
)

cd /d "%SCRIPT_DIR%"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -STA -File "%SCRIPT_PATH%"
set "EXITCODE=%ERRORLEVEL%"
if not "%EXITCODE%"=="0" (
    echo.
    echo Startup failed. Press any key to close this window.
    pause >nul
)
exit /b %EXITCODE%
