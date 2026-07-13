@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "EXE=%~dp0bin\Release\net48\CodexUsageMonitor.exe"
if not exist "%EXE%" set "EXE=%~dp0tmp-build\CodexUsageMonitor.exe"

if exist "%EXE%" (
    "%EXE%" --exit >nul 2>&1
    ping -n 2 127.0.0.1 >nul
)

taskkill /F /IM CodexUsageMonitor.exe >nul 2>&1
ping -n 2 127.0.0.1 >nul

call "%~dp0build.bat" || exit /b 1

if exist "%~dp0bin\Release\net48\CodexUsageMonitor.exe" (
    start "" "%~dp0bin\Release\net48\CodexUsageMonitor.exe"
    exit /b 0
)

start "" "%~dp0tmp-build\CodexUsageMonitor.exe"
exit /b 0
