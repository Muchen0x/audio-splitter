@echo off
rem Launch from source: incremental Release build, then run the freshly built exe
rem under bin\Release\net48. The root folder deliberately keeps no exe/ini copy -
rem a copy there is what gets double-clicked long after the source has moved on.
rem
rem No arguments  -> start the GUI and return immediately.
rem With args     -> CLI mode (--selftest / --list / --volprobe ...), attached, exit code passed through.
setlocal
cd /d "%~dp0"

set "EXE=bin\Release\net48\SplitterGUI.exe"

echo [launch] building from source...
dotnet build StereoSplitter.csproj -c Release --nologo -v q
if not errorlevel 1 goto :launch

rem A build can fail simply because a running instance holds the exe open.
rem In that case the running binary IS the current build - just activate it.
tasklist /fi "imagename eq SplitterGUI.exe" 2>nul | find /i "SplitterGUI.exe" >nul
if errorlevel 1 (
    echo   ERROR: build failed, nothing launched.
    pause
    exit /b 1
)
echo   NOTE: build skipped - SplitterGUI.exe is running; activating that instance.

:launch
if not exist "%EXE%" (
    echo   ERROR: %EXE% not found.
    pause
    exit /b 1
)

if not "%~1"=="" (
    "%EXE%" %*
    exit /b %errorlevel%
)

start "" "%EXE%"
exit /b 0
