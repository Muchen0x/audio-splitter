@echo off
setlocal
cd /d "%~dp0"

echo [1/3] build + verify embedded resources...
call build.cmd
if errorlevel 1 exit /b 1

echo [2/3] check for uncommitted source changes...
git diff --quiet -- "*.cs" "*.csproj" build.cmd
if errorlevel 1 (
    echo   WARNING: source has uncommitted changes - the exe does not match the repo.
)

echo [3/3] log folder size...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$f=Get-ChildItem log\*.log -ErrorAction SilentlyContinue; $n=($f | Measure-Object -Property Length -Sum).Sum; if (-not $n) { $n=0 }; Write-Host ('  log/ ' + $f.Count + ' files, ' + [math]::Round($n/1MB,1) + ' MB')"

echo.
echo VERIFY OK
exit /b 0
