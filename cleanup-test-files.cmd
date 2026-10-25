@echo off
chcp 65001 >nul
setlocal
set "ROOT=%~dp0"
set "DEV=%ROOT%..\.devtools"

echo This deletes files created during development:
echo   1) ..\.devtools\writetest.bin                      (500 MB test write)
echo   2) ..\.devtools\cache\dotnet-sdk-8.0.426-win-x64.zip (272 MB installer, already extracted)
echo.
echo KEPT: ..\.devtools\dotnet  (the .NET SDK itself, needed by build-local.cmd)
echo.
set /p CONFIRM=Type Y then Enter to delete:
if /I not "%CONFIRM%"=="Y" (
  echo Cancelled. Nothing was deleted.
  pause
  exit /b 0
)

echo.
if exist "%DEV%\writetest.bin" (
  del /f /q "%DEV%\writetest.bin"
  echo deleted: writetest.bin
) else (
  echo skipped: writetest.bin not found
)

if exist "%DEV%\cache\dotnet-sdk-8.0.426-win-x64.zip" (
  del /f /q "%DEV%\cache\dotnet-sdk-8.0.426-win-x64.zip"
  echo deleted: dotnet-sdk-8.0.426-win-x64.zip
) else (
  echo skipped: sdk zip not found
)

echo.
powershell -NoProfile -Command "Get-PSDrive D | ForEach-Object { 'D: {0:N1} GB free' -f ($_.Free/1GB) }"
echo.
echo Done.
pause
