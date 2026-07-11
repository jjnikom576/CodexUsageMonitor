@echo off
setlocal EnableExtensions
cd /d "%~dp0"
if exist "%~dp0bin\Release\net48\CodexUsageMonitor.exe" (
    start "" "%~dp0bin\Release\net48\CodexUsageMonitor.exe"
    exit /b 0
)
call "%~dp0build.bat" || exit /b 1
start "" "%~dp0bin\Release\net48\CodexUsageMonitor.exe"
exit /b 0
