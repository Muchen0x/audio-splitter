@echo off
setlocal
cd /d "%~dp0"

echo [1/4] build + verify embedded resources...
call build.cmd
if errorlevel 1 (
    echo   VERIFY FAILED: build.cmd failed.
    exit /b 1
)

echo [2/4] route selftest (exit code gate)...
rem Resolve the Chinese-named exe by wildcard (no non-ASCII bytes in this .cmd).
set "EXE="
for %%F in ("bin\Release\net48\*.exe") do (
    if /i not "%%~nxF"=="uidump.exe" if /i not "%%~nxF"=="SplitterGUI.exe" set "EXE=%%~F"
)
if not defined EXE (
    echo   VERIFY FAILED: main exe not found in bin\Release\net48.
    exit /b 1
)
"%EXE%" --selftest
if errorlevel 1 (
    echo   VERIFY FAILED: selftest returned non-zero - see bin\Release\net48\log\selftest-routing.txt
    exit /b 1
)

echo [3/4] source tree must match HEAD (no untracked/staged/unstaged source changes)...
rem F-28: porcelain covers untracked + staged + unstaged; git diff --quiet alone missed
rem untracked files (e.g. a new PcmRingBuffer.cs that the SDK still compiles).
for /f "delims=" %%L in ('git status --porcelain -- "*.cs" "*.csproj" build.cmd verify.cmd SplitterGUI.cmd .github\workflows\build.yml') do (
    echo   VERIFY FAILED: source differs from repo - exe may not match HEAD:
    echo     %%L
    exit /b 1
)

echo [4/4] log folder size...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$f=Get-ChildItem log\*.log -ErrorAction SilentlyContinue; $n=($f | Measure-Object -Property Length -Sum).Sum; if (-not $n) { $n=0 }; Write-Host ('  log/ ' + $f.Count + ' files, ' + [math]::Round($n/1MB,1) + ' MB')"

echo.
echo VERIFY OK
exit /b 0
