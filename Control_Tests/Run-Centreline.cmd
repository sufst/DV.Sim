@echo off
setlocal
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0Export-Centreline.ps1" %*
set "result=%errorlevel%"
pause
exit /b %result%
