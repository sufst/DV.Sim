@echo off
setlocal

if not exist "%~dp0FSDS.exe" (
    echo FSDS.exe is missing. Run this launcher from the complete DV.Sim checkout.
    pause
    exit /b 1
)

if /i "%~1"=="--fullscreen" (
    start "" /D "%~dp0" "%~dp0FSDS.exe" -fullscreen
) else (
    start "" /D "%~dp0" "%~dp0FSDS.exe" -windowed -ResX=1280 -ResY=720
)
