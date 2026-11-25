@echo off
rem Launch from source: incremental Release build, then run the freshly built exe
rem under bin\Release\net48. The root folder deliberately keeps no exe/ini copy -
rem a copy there is what gets double-clicked long after the source has moved on.
rem
rem No arguments  -> start the GUI and return immediately.
rem With args     -> CLI mode (--selftest / --list / --volprobe ...), attached, exit code passed through.
setlocal
cd /d "%~dp0"

rem Resolve the Chinese-named exe by wildcard so this .cmd stays pure ASCII.
rem Exclude the layout probe and any leftover binary from before the rename.
set "EXE="
set "EXENAME="
for %%F in ("bin\Release\net48\*.exe") do (
    if /i not "%%~nxF"=="uidump.exe" if /i not "%%~nxF"=="SplitterGUI.exe" (
        set "EXE=%%~F"
        set "EXENAME=%%~nxF"
    )
)

echo [launch] building from source...
dotnet build StereoSplitter.csproj -c Release --nologo -v q
if not errorlevel 1 goto :resolve_after_build

rem A build can fail simply because a running instance holds the exe open.
rem In that case the running binary IS the current build - just activate it.
if not defined EXENAME goto :build_failed
tasklist 2>nul | find /i "%EXENAME%" >nul
if errorlevel 1 goto :build_failed
echo   NOTE: build skipped - %EXENAME% is running; activating that instance.
goto :launch

:build_failed
echo   ERROR: build failed, nothing launched.
pause
exit /b 1

:resolve_after_build
set "EXE="
for %%F in ("bin\Release\net48\*.exe") do (
    if /i not "%%~nxF"=="uidump.exe" if /i not "%%~nxF"=="SplitterGUI.exe" set "EXE=%%~F"
)

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
