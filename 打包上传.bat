@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "CURRENT_VERSION="
for /f "usebackq delims=" %%V in (`powershell -NoProfile -Command ". '.\tools\Version.ps1'; Get-IronNestFcsVersion -RepoRoot (Get-Location).Path"`) do set "CURRENT_VERSION=%%V"

set "NEXT_VERSION="
if defined CURRENT_VERSION (
    for /f "usebackq delims=" %%V in (`powershell -NoProfile -Command "$m=[regex]::Match('%CURRENT_VERSION%','^(\d+)\.(\d+)\.(\d+)$'); if($m.Success){'{0}.{1}.{2}' -f $m.Groups[1].Value,$m.Groups[2].Value,([int]$m.Groups[3].Value+1)}"`) do set "NEXT_VERSION=%%V"
)

echo.
echo IronNestFCS Smart Release
echo Leave the version blank to automatically increment the patch version.
if defined NEXT_VERSION (
    echo Current: %CURRENT_VERSION% ^> Next: %NEXT_VERSION%
) else (
    echo Current version will be detected by tools\Release.ps1.
)
echo.

set "VERSION="
if defined NEXT_VERSION (
    set /p "VERSION=Release version (blank = auto %NEXT_VERSION%): "
) else (
    set /p "VERSION=Release version (blank = auto): "
)

echo.
if "%VERSION%"=="" (
    powershell -NoProfile -ExecutionPolicy Bypass -File ".\tools\Release.ps1"
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File ".\tools\Release.ps1" "%VERSION%"
)

set "EXITCODE=%ERRORLEVEL%"
echo.
if not "%EXITCODE%"=="0" (
    echo Release failed. Exit code: %EXITCODE%
) else (
    echo Release completed successfully.
)
echo.
pause
exit /b %EXITCODE%
