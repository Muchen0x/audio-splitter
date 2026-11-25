@echo off
setlocal
cd /d "%~dp0"

echo [1/4] restore...
dotnet restore StereoSplitter.csproj --nologo
if errorlevel 1 goto :fail

echo [2/4] build Release...
dotnet build StereoSplitter.csproj -c Release --nologo --no-restore
if errorlevel 1 goto :fail

rem Exe name is Chinese; resolve it by wildcard so this .cmd stays pure ASCII
rem (cmd.exe reads batch files with the console code page, not UTF-8).
rem Exclude the layout probe and any leftover binary from before the rename.
set "EXE="
for %%F in ("bin\Release\net48\*.exe") do (
    if /i not "%%~nxF"=="uidump.exe" if /i not "%%~nxF"=="SplitterGUI.exe" set "EXE=%%~F"
)
if not defined EXE (
    echo   ERROR: main exe not found in bin\Release\net48
    goto :fail
)

echo [3/4] verify embedded audio resources...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$f=Get-ChildItem 'bin\Release\net48\*.exe' | Where-Object { $_.BaseName -ne 'uidump' -and $_.BaseName -ne 'SplitterGUI' } | Select-Object -First 1; if (-not $f) { Write-Host '  ERROR: main exe missing'; exit 1 }; $a=[Reflection.Assembly]::ReflectionOnlyLoadFrom($f.FullName); $r=$a.GetManifestResourceNames(); Write-Host ('  embedded: ' + ($r -join ', ')); if ($r -notcontains 'NAudio.Core.dll' -or $r -notcontains 'NAudio.Wasapi.dll') { Write-Host '  ERROR: embedded resources missing'; exit 1 }"
if errorlevel 1 goto :fail

echo [4/4] guard: no stray NAudio dll in project root...
if exist "NAudio.Core.dll" goto :stray
if exist "NAudio.Wasapi.dll" goto :stray
goto :ok

:stray
echo   ERROR: NAudio*.dll found in project root.
echo          CLR probing loads the root dll BEFORE the embedded fallback, so
echo          the embedded copy is never exercised and the two can silently
echo          differ. Delete them - they belong in bin\Release\net48 only.
goto :fail

:ok
echo.
echo OK: %EXE%  (launch with SplitterGUI.cmd)
exit /b 0

:fail
echo.
echo BUILD FAILED.
exit /b 1
