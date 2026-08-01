@echo off
cd /d "%~dp0"
if not exist "qq-monitor-ui.exe" call "build-ui.bat"
if not exist "qq-monitor-ui.exe" exit /b 1
start "" "qq-monitor-ui.exe"
