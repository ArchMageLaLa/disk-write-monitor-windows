@echo off
chcp 65001 >nul
setlocal
set "ROOT=%~dp0"
set "DOTNET_ROOT=%ROOT%..\.devtools\dotnet"
set "PATH=%DOTNET_ROOT%;%PATH%"
set "DOTNET_CLI_HOME=%ROOT%..\.devtools\home"
set "NUGET_PACKAGES=%ROOT%..\.devtools\nuget"
set "DOTNET_NOLOGO=1"

if not exist "%DOTNET_ROOT%\dotnet.exe" (
  echo [ERROR] local .NET SDK not found:
  echo         %DOTNET_ROOT%
  echo Install the .NET 8 SDK and run this again.
  pause
  exit /b 1
)

echo Building DiskWriteMonitor...
"%DOTNET_ROOT%\dotnet.exe" publish "%ROOT%src\DiskWriteMonitor\DiskWriteMonitor.csproj" -c Release -o "%ROOT%dist"
if errorlevel 1 (
  echo.
  echo BUILD FAILED
  pause
  exit /b 1
)

echo.
echo ======================================
echo Done.
echo   %ROOT%dist\DiskWriteMonitor.exe
echo ======================================
pause
