@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\start.ps1" %*
set "launchExit=%errorlevel%"
if not "%launchExit%"=="0" (
    echo.
    echo Sunday Rivalry could not start. The diagnostic log path is shown above.
    pause
)
exit /b %launchExit%
