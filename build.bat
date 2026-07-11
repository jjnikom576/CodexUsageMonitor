@echo off
setlocal EnableExtensions
cd /d "%~dp0"
dotnet build -c Release
exit /b %ERRORLEVEL%
