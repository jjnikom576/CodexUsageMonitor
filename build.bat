@echo off
setlocal EnableExtensions
cd /d "%~dp0"
dotnet build -c Release -o "%~dp0tmp-build" || exit /b 1
if not exist "%~dp0bin\Release\net48" mkdir "%~dp0bin\Release\net48" >nul 2>&1
copy /y "%~dp0tmp-build\CodexUsageMonitor.exe" "%~dp0bin\Release\net48\" >nul 2>&1
exit /b 0
