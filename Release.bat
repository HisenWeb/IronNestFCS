@echo off
setlocal EnableExtensions
cd /d "%~dp0"

echo.
echo IronNestFCS Smart Release
echo Leave the version blank to automatically increment the patch version.
echo Example: current 1.1.3 ^> next 1.1.4
echo.

set "VERSION="
set /p "VERSION=Release version (blank = auto): "

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
