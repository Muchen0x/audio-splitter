@echo off
setlocal
cd /d "%~dp0"

echo [1/4] restore...
dotnet restore StereoSplitter.csproj --nologo
if errorlevel 1 goto :fail

echo [2/4] build Release...
dotnet build StereoSplitter.csproj -c Release --nologo --no-restore
if errorlevel 1 goto :fail

echo [3/4] verify embedded audio resources...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$a=[Reflection.Assembly]::ReflectionOnlyLoadFrom('bin\Release\net48\SplitterGUI.exe'); $r=$a.GetManifestResourceNames(); Write-Host ('  embedded: ' + ($r -join ', ')); if ($r -notcontains 'NAudio.Core.dll' -or $r -notcontains 'NAudio.Wasapi.dll') { Write-Host '  ERROR: embedded resources missing'; exit 1 }"
if errorlevel 1 goto :fail

echo [4/4] publish to root (run entry point, avoids running stale exe)...
copy /y "bin\Release\net48\SplitterGUI.exe" "SplitterGUI.exe" >nul
if errorlevel 1 (
    echo   ERROR: cannot overwrite SplitterGUI.exe - close the running app and retry.
    goto :fail
)
fc /b "SplitterGUI.exe" "bin\Release\net48\SplitterGUI.exe" >nul
if errorlevel 1 (
    echo   ERROR: root exe differs from build output.
    goto :fail
)

echo.
echo OK: SplitterGUI.exe (root synced with bin\Release\net48)
exit /b 0

:fail
echo.
echo BUILD FAILED.
exit /b 1
