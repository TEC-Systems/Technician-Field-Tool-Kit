@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
set "SCRIPT_PATH=%SCRIPT_DIR%TEC-Systems-OnlineTroubleshooting-Backend.ps1"

if not exist "%SCRIPT_PATH%" (
    echo Could not find "%SCRIPT_PATH%"
    pause
    exit /b 1
)

if "%OPENAI_API_KEY%"=="" (
    echo OPENAI_API_KEY is not set in this console or Windows environment.
    echo Set the key on the backend host before starting this service.
    echo.
)

cd /d "%SCRIPT_DIR%"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_PATH%"
