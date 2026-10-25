@echo off
chcp 65001 >nul
setlocal
set "EXE=%~dp0..\dist\DiskWriteMonitor.exe"
set "OUT=%~dp0..\docs\preview-admin.png"

if not exist "%EXE%" (
  echo [ERROR] app not found: %EXE%
  echo Run build-local.cmd first.
  pause
  exit /b 1
)

echo Requesting administrator rights, then sampling for 6 seconds...
echo Please click "Yes" on the UAC dialog.
echo.
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%EXE%' -ArgumentList '--render','%OUT%' -Verb RunAs -Wait"
echo.
if exist "%OUT%" (
  echo Screenshot saved: docs\preview-admin.png
) else (
  echo [WARN] screenshot not created. Did you cancel the UAC prompt?
)
pause
