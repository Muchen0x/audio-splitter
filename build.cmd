@echo off
setlocal
cd /d "%~dp0"

echo [1/3] restore...
dotnet restore StereoSplitter.csproj --nologo
if errorlevel 1 goto :fail

echo [2/3] build Release...
dotnet build StereoSplitter.csproj -c Release --nologo --no-restore
if errorlevel 1 goto :fail

echo [3/3] verify embedded audio resources...
powershell -NoProfile -Command "$a=[Reflection.Assembly]::ReflectionOnlyLoadFrom('bin\Release\net48\SplitterGUI.exe'); $r=$a.GetManifestResourceNames(); Write-Host ('  embedded: ' + ($r -join ', ')); if ($r -notcontains 'NAudio.Core.dll' -or $r -notcontains 'NAudio.Wasapi.dll') { Write-Host '  ERROR: embedded resources missing'; exit 1 }"
if errorlevel 1 goto :fail

echo.
echo OK: bin\Release\net48\SplitterGUI.exe
exit /b 0

:fail
echo.
echo BUILD FAILED.
exit /b 1
