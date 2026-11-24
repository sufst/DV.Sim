@echo off
setlocal
if /i "%~1"=="--fullscreen" (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Run-Simulator.ps1" -Fullscreen
) else (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Run-Simulator.ps1"
)
if errorlevel 1 pause
