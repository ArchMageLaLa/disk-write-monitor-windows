@echo off
chcp 65001 >nul
setlocal
set "PROBE=%~dp0dist\DiskProbe.exe"

if not exist "%PROBE%" (
  echo [ERROR] probe not found:
  echo         %PROBE%
  echo Build it first, then run this again.
  pause
  exit /b 1
)

echo.
echo Requesting administrator rights, please click "Yes" on the UAC dialog.
echo.
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%PROBE%' -ArgumentList '--pause' -WorkingDirectory '%~dp0dist' -Verb RunAs -Wait"

echo.
echo ============================================
echo Probe finished.
echo Report file: tools\probe\dist\report-admin.txt
echo Send that file back.
echo ============================================
pause
