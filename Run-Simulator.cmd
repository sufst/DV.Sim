@echo off
setlocal

if not exist "%~dp0FSDS.exe" (
    echo FSDS.exe is missing. Run this launcher from the complete DV.Sim checkout.
    pause
    exit /b 1
)

if /i "%~1"=="--fullscreen" (
    start "" powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0Launcher\RunSettings.ps1" -Fullscreen
) else (
    start "" powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0Launcher\RunSettings.ps1"
)
