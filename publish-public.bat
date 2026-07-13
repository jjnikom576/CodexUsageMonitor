@echo off
setlocal EnableExtensions
cd /d "%~dp0"
set "VERSION=1.1.1"
set "PUBLISH=%~dp0publish\public"
set "ZIP=%~dp0publish\CodexUsageMonitor-v%VERSION%.zip"
set "SHA=%~dp0publish\CodexUsageMonitor-v%VERSION%.sha256.txt"

call "%~dp0build.bat" || exit /b 1

if exist "%PUBLISH%" rmdir /s /q "%PUBLISH%"
mkdir "%PUBLISH%" || exit /b 1

copy /y "%~dp0tmp-build\CodexUsageMonitor.exe" "%PUBLISH%\" >nul || copy /y "%~dp0bin\Release\net48\CodexUsageMonitor.exe" "%PUBLISH%\" >nul || exit /b 1

if exist "%ZIP%" del /f /q "%ZIP%"
powershell -NoProfile -Command "Compress-Archive -Path '%PUBLISH%\*' -DestinationPath '%ZIP%' -Force" || exit /b 1

powershell -NoProfile -Command "$h = Get-FileHash -Path '%ZIP%' -Algorithm SHA256; Set-Content -Path '%SHA%' -Value ($h.Hash + '  ' + (Split-Path '%ZIP%' -Leaf)) -Encoding ASCII" || exit /b 1

copy /y "%ZIP%" "%USERPROFILE%\Desktop\" >nul || exit /b 1

echo.
echo Published: %ZIP%
echo Desktop:   %USERPROFILE%\Desktop\CodexUsageMonitor-v%VERSION%.zip
exit /b 0
